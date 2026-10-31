using Bibliotaph.Api.Contracts;

namespace Bibliotaph.Web.Services;

/// <summary>Settings › Backup's restore.</summary>
public sealed class BackupClient(ApiHttp http)
{
    public Task<List<KeptBackupInfo>> KeptBackupsAsync(CancellationToken ct = default) =>
        http.SendAsync<List<KeptBackupInfo>>(HttpMethod.Get, "api/library/backups", null, ct);

    /// <summary>Reads a kept backup for restoring; nothing changes until <see cref="RestoreAsync"/>.</summary>
    public Task<RestoreRead> ReadKeptBackupAsync(string name, CancellationToken ct = default) =>
        http.SendAsync<RestoreRead>(HttpMethod.Post, $"api/library/restore?backup={Uri.EscapeDataString(name)}", null, ct);

    /// <summary>Reads a backup the user chose on this computer, made here or by the Windows app.</summary>
    public Task<RestoreRead> ReadBackupAsync(Stream zip, CancellationToken ct = default) =>
        http.SendAsync<RestoreRead>(HttpMethod.Post, "api/library/restore", ApiHttp.Content(zip, "application/zip"), ct);

    public Task<ActionResult> RestoreAsync(string token, CancellationToken ct = default) =>
        http.SendAsync<ActionResult>(HttpMethod.Post, $"api/library/restore/{Uri.EscapeDataString(token)}", null, ct);

    public Task CancelRestoreAsync(string token, CancellationToken ct = default) =>
        http.SendAsync(HttpMethod.Delete, $"api/library/restore/{Uri.EscapeDataString(token)}", null, ct);
}
