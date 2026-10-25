using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Components.WebAssembly.Http;

namespace Spike.Web;

/// <summary>SHA-256 of a book in .NET, in the worker: the managed hash the pipeline would use if it hashed in C#.</summary>
[SupportedOSPlatform("browser")]
public static partial class HashBench
{
    static readonly HttpClient Http = new();

    [JSExport]
    public static async Task<string> Sha256(string url)
    {
        var watch = Stopwatch.StartNew();
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.SetBrowserResponseStreamingEnabled(true);
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1 << 20];
        long bytes = 0, hashTicks = 0;
        for (int read; (read = await stream.ReadAsync(buffer)) > 0;)
        {
            var started = Stopwatch.GetTimestamp();
            hash.AppendData(buffer, 0, read);
            hashTicks += Stopwatch.GetTimestamp() - started;
            bytes += read;
        }
        var sha256 = Convert.ToHexStringLower(hash.GetHashAndReset());
        return JsonSerializer.Serialize(new { bytes, ms = watch.ElapsedMilliseconds, hashMs = (long)Stopwatch.GetElapsedTime(0, hashTicks).TotalMilliseconds, sha256 });
    }
}
