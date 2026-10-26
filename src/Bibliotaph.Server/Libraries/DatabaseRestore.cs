using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace Bibliotaph.Server.Libraries;

/// <summary>Brings a database back from its durable copy when the replica's disk doesn't have it.</summary>
public interface IDatabaseRestore
{
    /// <summary>
    /// Restores <paramref name="relativePath"/>, the file's path under the data root with forward slashes
    /// (users/&lt;id&gt;/catalog.db), to <paramref name="target"/>, which is left alone when it already exists.
    /// </summary>
    Task RestoreAsync(string relativePath, string target, CancellationToken ct);
}

/// <summary>Local development and tests: the disk is the only copy.</summary>
public sealed class NoRestore : IDatabaseRestore
{
    public Task RestoreAsync(string relativePath, string target, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// Restores from Litestream's replica in Blob storage. The container runs <c>litestream replicate</c>, which watches the
/// data root and replicates any database that appears there, so a restored file picks up replicating where it left off.
/// </summary>
public sealed class LitestreamRestore(IOptions<ServerOptions> options, ServerPaths paths, ILogger<LitestreamRestore> log) : IDatabaseRestore
{
    public async Task RestoreAsync(string relativePath, string target, CancellationToken ct)
    {
        if (File.Exists(target)) return;
        var replica = options.Value.ReplicaUrl ?? throw new InvalidOperationException("Server:ReplicaUrl is not set.");

        Directory.CreateDirectory(paths.Staging);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        // Not *.db, so Litestream's watcher ignores it while it's being written.
        var staging = Path.Combine(paths.Staging, $"{Guid.NewGuid():N}.restoring");
        var started = Stopwatch.GetTimestamp();

        var start = new ProcessStartInfo(options.Value.LitestreamPath)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            ArgumentList = { "restore", "-if-replica-exists", "-o", staging, $"{replica.TrimEnd('/')}/{relativePath}" },
        };
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Litestream did not start.");
        var stderr = process.StandardError.ReadToEndAsync(ct);
        _ = process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Litestream could not restore {relativePath} (exit {process.ExitCode}): {(await stderr).Trim()}");

        // No replica yet: a new user, or a file never written. Nothing to put in place.
        if (!File.Exists(staging)) return;
#pragma warning disable RS0030 // The server's own data folder; books are never local files here.
        var bytes = new FileInfo(staging).Length;
        File.Move(staging, target);
#pragma warning restore RS0030
        log.LogInformation("Restored {Database} ({Megabytes:N1} MB) from its replica in {Milliseconds:N0} ms", relativePath,
            bytes / 1048576.0, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }
}
