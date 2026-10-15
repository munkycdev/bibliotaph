using System.Globalization;
using Bibliotaph.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bibliotaph.Processing;

/// <summary>
/// The small icons websites show in a browser tab, for the badge on a card owned elsewhere: D&amp;D Beyond's icon
/// beside "D&amp;D Beyond". They live in %LOCALAPPDATA%\Bibliotaph\cache\icons, one file per site, named by its host.
/// A site's icon is fetched once, from its /favicon.ico, the first time a card asks for it; a site without one is
/// asked again only after <see cref="RetryAfter"/>. Nothing is fetched while the "Show site icons" setting is off,
/// which the caller checks. Derived and disposable, like covers.
/// </summary>
public sealed class SiteIcons(AppPaths paths, HttpClient http, TimeProvider? clock = null, ILogger<SiteIcons>? log = null)
{
    /// <summary>An icon bigger than this isn't one: the site answered with something else.</summary>
    public const int MaxBytes = 256 * 1024;

    /// <summary>How long a site that had no icon is left alone before it is asked again.</summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromDays(7);

    const string MissingSuffix = ".none";

    static readonly string[] Extensions = [".ico", ".png", ".gif", ".jpg"];

    readonly TimeProvider _clock = clock ?? TimeProvider.System;
    readonly ILogger _log = (ILogger?)log ?? NullLogger.Instance;
    readonly Lock _lock = new();
    readonly Dictionary<string, Task<string?>> _fetching = [];

    public string Folder { get; } = Path.Combine(paths.Cache, "icons");

    /// <summary>
    /// The site a link is on, as icons are kept: its host in lower case without "www.", in its ASCII form. Null for
    /// anything but an http or https link, or a host that isn't only letters, digits, dots and hyphens.
    /// </summary>
    public static string? HostOf(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return null;
        var host = uri.IdnHost.ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal)) host = host[4..];
        return host.Length > 0 && host.Contains('.', StringComparison.Ordinal) && host.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '-')
            && !host.StartsWith('.') && !host.Contains("..", StringComparison.Ordinal)
            ? host
            : null;
    }

    /// <summary>The icon kept for a site, or null when there isn't one yet.</summary>
    public string? Find(string host) =>
        Extensions.Select(e => Path.Combine(Folder, host + e)).FirstOrDefault(File.Exists);

    /// <summary>
    /// The icon for the site a link is on: the one kept, or else fetched now and kept. Null when the site has none
    /// (or had none recently), or it couldn't be reached. Two cards asking at once share one fetch.
    /// </summary>
    public Task<string?> GetAsync(string url, CancellationToken ct = default)
    {
        if (HostOf(url) is not { } host) return Task.FromResult<string?>(null);
        if (Find(host) is { } kept) return Task.FromResult<string?>(kept);
        lock (_lock)
        {
            if (_fetching.TryGetValue(host, out var running)) return running;
            var task = FetchAsync(host, ct);
            _fetching[host] = task;
            _ = task.ContinueWith(_ => { lock (_lock) _fetching.Remove(host); }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return task;
        }
    }

    /// <summary>
    /// Keeps an icon the caller already has, such as the one the in-app browser shows for a site it visited. Returns
    /// where it was kept, or null when the bytes aren't an icon or an image.
    /// </summary>
    public string? Keep(string host, ReadOnlySpan<byte> image)
    {
        if (Extension(image) is not { } extension || image.Length > MaxBytes) return null;
        Directory.CreateDirectory(Folder);
        foreach (var other in Extensions.Where(e => e != extension).Select(e => Path.Combine(Folder, host + e)).Where(File.Exists)) File.Delete(other);
        var target = Path.Combine(Folder, host + extension);
        var temp = target + ".tmp";
        File.WriteAllBytes(temp, image.ToArray());
        File.Move(temp, target, overwrite: true);
        var missing = Path.Combine(Folder, host + MissingSuffix);
        if (File.Exists(missing)) File.Delete(missing);
        return target;
    }

    async Task<string?> FetchAsync(string host, CancellationToken ct)
    {
        var missing = Path.Combine(Folder, host + MissingSuffix);
        if (File.Exists(missing) && ReadStamp(missing) is { } stamp && _clock.GetUtcNow() - stamp < RetryAfter) return null;
        try
        {
            using var response = await http.GetAsync(new Uri($"https://{host}/favicon.ico"), HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.IsSuccessStatusCode && response.Content.Headers.ContentLength is null or <= MaxBytes)
            {
                var bytes = await response.Content.ReadAsByteArrayAsync(ct);
                if (Keep(host, bytes) is { } kept)
                {
                    _log.LogInformation("Kept the site icon of {Host}", host);
                    return kept;
                }
            }
            _log.LogInformation("{Host} has no site icon Bibliotaph can show ({Status})", host, (int)response.StatusCode);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            _log.LogInformation("{Host}'s site icon couldn't be fetched: {Message}", host, ex.Message);
        }
        Directory.CreateDirectory(Folder);
        File.WriteAllText(missing, _clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
        return null;
    }

    static DateTimeOffset? ReadStamp(string path) =>
        DateTimeOffset.TryParse(File.ReadAllText(path), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var stamp) ? stamp : null;

    /// <summary>The file extension an icon's bytes call for, by their first bytes; null for anything else, such as a web page.</summary>
    static string? Extension(ReadOnlySpan<byte> image) =>
        image.StartsWith((byte[])[0x00, 0x00, 0x01, 0x00]) ? ".ico"
        : image.StartsWith((byte[])[0x89, (byte)'P', (byte)'N', (byte)'G']) ? ".png"
        : image.StartsWith("GIF8"u8) ? ".gif"
        : image.StartsWith((byte[])[0xFF, 0xD8, 0xFF]) ? ".jpg"
        : null;
}
