using System.Globalization;
using System.Text.RegularExpressions;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace Bibliotaph.Server.Libraries;

/// <summary>A backup kept for a user: the weekly ones, and the one made before each restore.</summary>
public sealed record KeptBackup(string Name, DateTimeOffset Created, long Bytes)
{
    public bool Automatic => Name.StartsWith(BackupNames.Weekly, StringComparison.Ordinal);
}

/// <summary>
/// How kept backups are named: their kind, then their UTC time, as the Windows app names its automatic backups. Only
/// these names are ever read or written, so a name from a request can't reach anything else.
/// </summary>
public static partial class BackupNames
{
    /// <summary>The weekly backup: only these are ever deleted, the oldest first past <see cref="Processing.BackupService.KeptAutomatic"/>.</summary>
    public const string Weekly = Processing.BackupService.AutomaticPrefix;

    /// <summary>The catalog as it was just before a restore replaced it, kept until the account closes.</summary>
    public const string BeforeRestore = "before-restore-";

    const string StampFormat = "yyyyMMdd-HHmmss";

    public static string For(string kind, DateTimeOffset when) =>
        kind + when.UtcDateTime.ToString(StampFormat, CultureInfo.InvariantCulture) + ".zip";

    /// <summary>When a kept backup was made, or null when the name isn't one of a kept backup.</summary>
    public static DateTimeOffset? Created(string name) =>
        Name().Match(name) is { Success: true } match
        && DateTime.TryParseExact(match.Groups[1].Value, StampFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var created)
            ? new DateTimeOffset(created, TimeSpan.Zero)
            : null;

    [GeneratedRegex(@"^(?:automatic-backup|before-restore)-(\d{8}-\d{6})\.zip$")]
    private static partial Regex Name();
}

/// <summary>
/// Where a user's backups outlive the server's disk, which a restart empties. Litestream keeps the databases as they
/// are now; these keep the catalog as it was, a week or a restore ago.
/// </summary>
public interface IBackupStore
{
    /// <summary>The user's kept backups, newest first.</summary>
    Task<IReadOnlyList<KeptBackup>> ListAsync(string userId, CancellationToken ct);

    Task UploadAsync(string userId, string name, Stream zip, CancellationToken ct);

    /// <summary>The backup's bytes to read, or null when it isn't kept.</summary>
    Task<Stream?> OpenAsync(string userId, string name, CancellationToken ct);

    Task DeleteAsync(string userId, string name, CancellationToken ct);
}

/// <summary>
/// On a developer's machine and in tests the disk is kept, so backups stay beside the user's pre-migration backups,
/// which closing the account deletes.
/// </summary>
#pragma warning disable RS0030 // The server's own copies of a user's catalog; books are never local files here.
public sealed class DiskBackupStore(ServerPaths paths) : IBackupStore
{
    string Folder(string userId) => Path.Combine(paths.UserBackups(userId), "kept");

    public Task<IReadOnlyList<KeptBackup>> ListAsync(string userId, CancellationToken ct)
    {
        var folder = Folder(userId);
        IReadOnlyList<KeptBackup> kept = !Directory.Exists(folder) ? [] :
        [
            .. new DirectoryInfo(folder).EnumerateFiles("*.zip")
                .Select(f => BackupNames.Created(f.Name) is { } created ? new KeptBackup(f.Name, created, f.Length) : null)
                .OfType<KeptBackup>().OrderByDescending(b => b.Created),
        ];
        return Task.FromResult(kept);
    }

    public async Task UploadAsync(string userId, string name, Stream zip, CancellationToken ct)
    {
        Directory.CreateDirectory(Folder(userId));
        var target = Path.Combine(Folder(userId), Checked(name));
        await using (var file = File.Create(target + ".partial")) await zip.CopyToAsync(file, ct);
        File.Move(target + ".partial", target, overwrite: true);
    }

    public Task<Stream?> OpenAsync(string userId, string name, CancellationToken ct)
    {
        var file = Path.Combine(Folder(userId), Checked(name));
        return Task.FromResult<Stream?>(File.Exists(file) ? File.OpenRead(file) : null);
    }

    public Task DeleteAsync(string userId, string name, CancellationToken ct)
    {
        File.Delete(Path.Combine(Folder(userId), Checked(name)));
        return Task.CompletedTask;
    }

    static string Checked(string name) => BackupNames.Created(name) is null ? throw new ArgumentException("Not a kept backup's name.", nameof(name)) : name;
}
#pragma warning restore RS0030

/// <summary>
/// Backups in a Blob container, under users/{user ID}/, beside the replicas and covers. The container is made the
/// first time it's needed, so a server set up before backups were kept needs nothing new.
/// </summary>
public sealed class BlobBackupStore(BlobContainerClient container) : IBackupStore
{
    readonly Lazy<Task> _made = new(() => container.CreateIfNotExistsAsync());

    public async Task<IReadOnlyList<KeptBackup>> ListAsync(string userId, CancellationToken ct)
    {
        await _made.Value;
        var prefix = Prefix(userId);
        var kept = new List<KeptBackup>();
        await foreach (var blob in container.GetBlobsAsync(prefix: prefix, cancellationToken: ct))
        {
            var name = blob.Name[prefix.Length..];
            if (BackupNames.Created(name) is { } created) kept.Add(new KeptBackup(name, created, blob.Properties.ContentLength ?? 0));
        }
        return [.. kept.OrderByDescending(b => b.Created)];
    }

    public async Task UploadAsync(string userId, string name, Stream zip, CancellationToken ct)
    {
        await _made.Value;
        await Blob(userId, name).UploadAsync(zip, new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = "application/zip" } }, ct);
    }

    public async Task<Stream?> OpenAsync(string userId, string name, CancellationToken ct)
    {
        await _made.Value;
        try
        {
            return await Blob(userId, name).OpenReadAsync(cancellationToken: ct);
        }
        catch (RequestFailedException ex) when (ex.Status == StatusCodes.Status404NotFound)
        {
            return null;
        }
    }

    public async Task DeleteAsync(string userId, string name, CancellationToken ct)
    {
        await _made.Value;
        await Blob(userId, name).DeleteIfExistsAsync(cancellationToken: ct);
    }

    BlobClient Blob(string userId, string name) =>
        BackupNames.Created(name) is null ? throw new ArgumentException("Not a kept backup's name.", nameof(name)) : container.GetBlobClient(Prefix(userId) + name);

    static string Prefix(string userId) => $"users/{UserIds.Require(userId)}/";
}
