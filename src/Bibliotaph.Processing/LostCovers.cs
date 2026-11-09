using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bibliotaph.Processing;

/// <summary>
/// Gets back the covers that books imported from online libraries have lost, as on a device that got the books before
/// their covers travelled with the library: each is downloaded again from the address its library page showed, on the
/// service's own image server, which gives it without a sign-in. Only an https address on one of the services'
/// <see cref="IOnlineLibraryReader.CoverSites"/> is fetched, only an image is kept, and nothing else of the service
/// is visited. Runs once a device starts and when library sync brings a book's cover address.
/// </summary>
public sealed class LostCovers(EntryStore entries, CoverCache covers, IImageCodec images, MetadataProjector projector, HttpClient http,
    ILogger<LostCovers>? log = null) : IAsyncDisposable
{
    /// <summary>A cover bigger than this isn't one.</summary>
    public const int MaxBytes = 8 * 1024 * 1024;

    /// <summary>After this many downloads in a row fail, the service is taken to be out of reach until the next try.</summary>
    const int GiveUpAfter = 3;

    readonly ILogger _log = log ?? NullLogger<LostCovers>.Instance;

    /// <summary>A client for the downloads, naming Bibliotaph to the service, over <paramref name="handler"/> when given.</summary>
    public static HttpClient CreateClient(HttpMessageHandler? handler = null)
    {
        var client = handler is null ? new HttpClient() : new HttpClient(handler);
        client.Timeout = TimeSpan.FromSeconds(20);
        var version = typeof(LostCovers).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        client.DefaultRequestHeaders.UserAgent.Add(new System.Net.Http.Headers.ProductInfoHeaderValue("Bibliotaph", version));
        client.DefaultRequestHeaders.UserAgent.Add(new System.Net.Http.Headers.ProductInfoHeaderValue("(+https://bibliotaph.online)"));
        return client;
    }
    readonly SemaphoreSlim _one = new(1, 1);
    readonly CancellationTokenSource _stopping = new();
    Task _running = Task.CompletedTask;

    /// <summary>Gets back, in the background, the lost covers of every book, or of <paramref name="entryIds"/> only.</summary>
    public void Start(IReadOnlyCollection<EntryId>? entryIds = null)
    {
        if (_stopping.IsCancellationRequested) return;
        var before = _running;
        _running = Task.Run(async () =>
        {
            await before.ConfigureAwait(false);
            try
            {
                await RestoreAsync(entryIds, _stopping.Token);
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            {
                // Closing: the next start tries again.
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
            {
                _log.LogWarning(ex, "Getting back the covers of books from online libraries failed");
            }
        });
    }

    /// <summary>Downloads the covers these books have lost, keeps them and shows them on their cards. How many came back.</summary>
    public async Task<int> RestoreAsync(IReadOnlyCollection<EntryId>? entryIds = null, CancellationToken ct = default)
    {
        await _one.WaitAsync(ct);
        try
        {
            var lost = (await entries.GetCoverUrlsAsync(entryIds, ct)).Where(e => covers.FindElsewhere(e.SyncId) is null).ToList();
            if (lost.Count == 0) return 0;
            var back = new List<EntryId>();
            var failed = 0;
            foreach (var (entryId, syncId, url) in lost)
            {
                if (await DownloadAsync(url, ct) is not { } jpeg)
                {
                    if (++failed >= GiveUpAfter) break;
                    continue;
                }
                failed = 0;
                // The cover may have come by sync meanwhile.
                if (covers.FindElsewhere(syncId) is not null) continue;
                covers.KeepImported(syncId, jpeg);
                back.Add(entryId);
            }
            if (back.Count > 0) await projector.ProjectAsync(back, ct);
            _log.LogInformation("Got back {Back} of the {Lost} covers books from online libraries had lost", back.Count, lost.Count);
            return back.Count;
        }
        finally
        {
            _one.Release();
        }
    }

    /// <summary>The cover at <paramref name="url"/> at cover size, or null when it isn't one Bibliotaph may fetch or can read.</summary>
    public async Task<byte[]?> DownloadAsync(string url, CancellationToken ct = default)
    {
        if (Allowed(url) is not { } uri) return null;
        try
        {
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) != true
                || response.Content.Headers.ContentLength > MaxBytes)
                return null;
            await using var body = await response.Content.ReadAsStreamAsync(ct);
            using var copy = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = await body.ReadAsync(buffer, ct)) > 0)
            {
                if (copy.Length + read > MaxBytes) return null;
                copy.Write(buffer, 0, read);
            }
            copy.Position = 0;
            return images.Thumbnail(copy, CoverCache.Width);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException || ex is TaskCanceledException && !ct.IsCancellationRequested)
        {
            _log.LogInformation("A cover from an online library couldn't be downloaded: {Message}", ex.Message);
            return null;
        }
    }

    /// <summary>The address, when it is an https one on a site an online library's covers come from; else null.</summary>
    public static Uri? Allowed(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.HostNameType != UriHostNameType.Dns
            || !string.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort)
            return null;
        var host = uri.IdnHost.ToLowerInvariant();
        return OnlineImports.Services.SelectMany(r => r.CoverSites).Any(site => host == site || host.EndsWith("." + site, StringComparison.Ordinal)) ? uri : null;
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        try
        {
            await _running;
        }
        catch (OperationCanceledException)
        {
            // Stopped as asked.
        }
        _stopping.Dispose();
        _one.Dispose();
    }
}
