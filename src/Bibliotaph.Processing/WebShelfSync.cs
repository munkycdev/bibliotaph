using System.Text.Json;
using Bibliotaph.Api.Contracts;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bibliotaph.Processing;

/// <summary>
/// Keeps a connected PC's library folders for the web library's (<see cref="WebLibraryPaths"/>) in step with its
/// list of files: every minute it asks whether the list changed, keeps it (<see cref="WebShelfCache"/>), adds a folder
/// new to the web library, removes one gone from it, and has them looked through again. A web library folder the user
/// removed here stays removed until this PC connects again. One in storage this PC connects itself, at or inside one of
/// its own library folders, is left out while that folder is in the library, since the PC reads the same files
/// directly (<see cref="ReapplyAsync"/>). Disconnected, the folders are removed, the list
/// forgotten and the downloaded books not kept offline deleted; the books keep their cards, notes and everything else
/// done with them, as a removed folder's do.
/// </summary>
public sealed class WebShelfSync(IWebBooks web, WebShelfCache shelf, SourceRootStore roots, IJobControl indexing, TimeProvider? clock = null,
    ILogger<WebShelfSync>? log = null, WebLibraryCopies? copies = null, SettingsStore? settings = null) : IHostedService, IDisposable
{
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The web library folders left out because this PC reads the same storage itself, as their PC paths: they come
    /// back when it stops, unlike a folder the user removed.
    /// </summary>
    public const string ReadDirectlyKey = "web.read-directly";

    /// <summary>How long after the start the first look runs: after the start's own work.</summary>
    public static readonly TimeSpan First = TimeSpan.FromSeconds(15);

    readonly TimeProvider _clock = clock ?? TimeProvider.System;
    readonly ILogger _log = log ?? NullLogger<WebShelfSync>.Instance;
    readonly SemaphoreSlim _gate = new(1, 1);
    readonly CancellationTokenSource _stopping = new();
    readonly Lock _lock = new();
    ITimer? _timer;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_lock) _timer = _clock.CreateTimer(_ => Fire(), null, First, Every);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _timer?.Dispose();
            _timer = null;
        }
        await _stopping.CancelAsync();
        await _gate.WaitAsync(cancellationToken);
        _gate.Release();
    }

    public void Dispose()
    {
        lock (_lock) _timer?.Dispose();
        _stopping.Dispose();
        _gate.Dispose();
    }

    /// <summary>This PC just connected or disconnected: looked at now rather than at the next minute.</summary>
    public void Nudge()
    {
        lock (_lock) _timer?.Change(TimeSpan.Zero, Every);
    }

    void Fire() => _ = Task.Run(async () =>
    {
        try
        {
            await SyncNowAsync(_stopping.Token);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            // Stopping.
        }
        catch (Exception ex)
        {
            // Tried again at the next minute.
            _log.LogWarning(ex, "Keeping the web library's folders in step failed");
        }
    });

    public async Task SyncNowAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!web.IsOn)
            {
                await ForgetAsync(ct);
                return;
            }
            var had = shelf.Current;
            var read = await web.ReadShelfAsync(had?.Version, ct);
            if (read.Shelf is not { } changed) return;
            shelf.Replace(changed);
            await ApplyAsync(changed, connected: had is null, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// This PC's own library folders changed: a folder in storage it now reads itself leaves the web library's copy of
    /// it out, and one it stopped reading brings that copy back.
    /// </summary>
    public async Task ReapplyAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (web.IsOn && shelf.Current is { } current) await ApplyAsync(current, connected: false, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    async Task ApplyAsync(WebShelf changed, bool connected, CancellationToken ct)
    {
        var all = await roots.ListWithRemovedAsync(ct);
        // Storage connected on this PC: the only other paths that start with a slash on Windows.
        var storageRoots = all.Where(r => r.Availability != SourceRootAvailability.RemovedByUser && !WebLibraryPaths.IsWebPath(r.Path) && r.Path.StartsWith('/'))
            .Select(r => r.Path).ToList();
        var wasReadDirectly = await ReadDirectlyAsync(ct);
        var readDirectly = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var listed = changed.Folders.Select(f => WebLibraryPaths.Of(f.Id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in changed.Folders)
        {
            var path = WebLibraryPaths.Of(folder.Id);
            var root = all.FirstOrDefault(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));
            if (WebShelfCache.ReadDirectly(folder, storageRoots))
            {
                // The PC's own folder has the same files, read straight from the provider, so the web's would be a second place for each.
                if (root is null || root.Availability != SourceRootAvailability.RemovedByUser || wasReadDirectly.Contains(path)) readDirectly.Add(path);
                if (root is not null && root.Availability != SourceRootAvailability.RemovedByUser)
                {
                    await roots.RemoveAsync(root.Id, ct);
                    _log.LogInformation("Web library folder {RootId} is read from storage on this PC instead", root.Id);
                }
                continue;
            }
            if (root is null || ((connected || wasReadDirectly.Contains(path)) && root.Availability == SourceRootAvailability.RemovedByUser))
            {
                root = await roots.AddAsync(path, ct);
                _log.LogInformation("Web library folder added as library folder {RootId}", root.Id);
            }
            if (root.Availability != SourceRootAvailability.RemovedByUser) indexing.RequestScan(root.Id);
        }
        if (!readDirectly.SetEquals(wasReadDirectly)) await SaveReadDirectlyAsync(readDirectly, ct);
        foreach (var gone in all.Where(r => r.Availability != SourceRootAvailability.RemovedByUser && WebLibraryPaths.IsWebPath(r.Path) && !listed.Contains(r.Path)))
        {
            await roots.RemoveAsync(gone.Id, ct);
            _log.LogInformation("Library folder {RootId} is no longer in the web library", gone.Id);
        }
    }

    async Task<IReadOnlySet<string>> ReadDirectlyAsync(CancellationToken ct)
    {
        if (settings is null || await settings.GetAsync(ReadDirectlyKey, ct) is not { Length: > 0 } json) return new HashSet<string>();
        try
        {
            return JsonSerializer.Deserialize<string[]>(json)?.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        }
        catch (JsonException ex)
        {
            _log.LogWarning(ex, "The web library folders read from storage on this PC couldn't be read");
            return new HashSet<string>();
        }
    }

    async Task SaveReadDirectlyAsync(IReadOnlySet<string> paths, CancellationToken ct)
    {
        if (settings is not null) await settings.SetAsync(ReadDirectlyKey, JsonSerializer.Serialize(paths.Order(StringComparer.Ordinal).ToArray()), ct);
    }

    async Task ForgetAsync(CancellationToken ct)
    {
        var folders = (await roots.ListAsync(ct)).Where(r => WebLibraryPaths.IsWebPath(r.Path)).ToList();
        foreach (var root in folders) await roots.RemoveAsync(root.Id, ct);
        if (folders.Count > 0) _log.LogInformation("Removed {Count} web library folders: this PC isn't connected", folders.Count);
        if (folders.Count == 0 && shelf.Current is null) return;
        await SaveReadDirectlyAsync(new HashSet<string>(), ct);
        shelf.Clear();
        if (copies is not null) await copies.ClearAsync(keepKept: true, ct);
    }
}
