using System.Security.Cryptography;
using System.Text.Json;
using Bibliotaph.Api.Contracts;
using Bibliotaph.Catalog;
using Bibliotaph.Processing;
using Bibliotaph.Server.Api;
using Bibliotaph.Server.Storage;

namespace Bibliotaph.Server.Libraries;

/// <summary>
/// Keeps the web library's shelf (<see cref="WebShelf"/>) in sync.db while the library is open: when it opens, a few
/// seconds after indexing moves and at least every minute, written only when something on it changed. Connected PCs ask
/// for it every minute, so it's read from sync.db as their sync files are, without opening the library; nothing on it
/// changes while the library is closed.
/// </summary>
public sealed class WebShelfPublisher(LibraryStore library, SourceRootStore roots, UserDrives drives, SyncFiles files, IIndexingStatus indexing,
    TimeProvider clock, ILogger<WebShelfPublisher> log) : IAsyncDisposable
{
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(1);

    /// <summary>Indexing says it moved many times a second while it works, so the shelf is built once a burst settles.</summary>
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(3);

    CancellationTokenSource? _stop;
    Task? _loop;
    TaskCompletionSource _moved = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The shelf as the catalog has it now.</summary>
    public async Task<WebShelf> BuildAsync(CancellationToken ct = default)
    {
        var byRoot = (await library.GetShelfFilesAsync(ct)).ToLookup(f => f.RootId);
        List<WebShelfFolder> folders =
        [
            .. (await roots.ListAsync(ct)).Select(root => new WebShelfFolder(root.Id, FolderText.Name(root.Path), FolderText.Location(root.Path, drives),
                [.. byRoot[root.Id].Select(f => new WebShelfFile(f.RelativePath.Replace('\\', '/'), f.ContentHash, f.SizeBytes, f.ModifiedUtc))])),
        ];
        // The version names the content, so the same shelf built again, here or after a restart, is the same version.
        var version = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(folders, JsonSerializerOptions.Web)))[..32];
        return new WebShelf(version, folders);
    }

    /// <summary>Builds the shelf and keeps it in sync.db, if it changed.</summary>
    public async Task PublishAsync(CancellationToken ct = default)
    {
        var shelf = await BuildAsync(ct);
        await files.WriteShelfAsync(shelf.Version, JsonSerializer.SerializeToUtf8Bytes(shelf, JsonSerializerOptions.Web), ct);
    }

    public void Start()
    {
        _stop = new CancellationTokenSource();
        indexing.Changed += OnIndexingChanged;
        _loop = RunAsync(_stop.Token);
    }

    void OnIndexingChanged(object? sender, EventArgs e) => Volatile.Read(ref _moved).TrySetResult();

    async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (true)
            {
                // Taken before building, so a move while it's built is caught by the next round.
                var moved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Volatile.Write(ref _moved, moved);
                try
                {
                    await PublishAsync(ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The PCs keep the shelf they have until the next try.
                    log.LogWarning(ex, "Keeping the web library's shelf for its PCs failed");
                }
                using (var wait = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    await Task.WhenAny(moved.Task, Task.Delay(Every, clock, wait.Token));
                    await wait.CancelAsync();
                }
                await Task.Delay(Settle, clock, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The library is closing.
        }
    }

    public async Task StopAsync()
    {
        if (_stop is null) return;
        indexing.Changed -= OnIndexingChanged;
        await _stop.CancelAsync();
        if (_loop is not null) await _loop;
        _stop.Dispose();
        _stop = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
