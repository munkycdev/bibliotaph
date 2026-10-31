using System.Globalization;
using System.Security.Claims;
using Bibliotaph.Api.Contracts;
using Bibliotaph.Catalog;
using Bibliotaph.Processing;
using Bibliotaph.Server.Auth;
using Bibliotaph.Server.Libraries;
using Bibliotaph.Server.Storage;
using Microsoft.AspNetCore.Http.Features;

namespace Bibliotaph.Server.Api;

/// <summary>
/// /api/library/backup and /api/library/export: Settings › Backup's downloads, the same files the Windows app's Back up
/// now, Export CSV and Export JSON save. A backup restores in the Windows app, its folders then found again or left
/// offline, so a user can always take their work away. /api/library/backups and /api/library/restore are the weekly
/// backups and Restore (<see cref="WebBackups"/>).
/// </summary>
public static class BackupApi
{
    public static void MapBackupApi(this RouteGroupBuilder api)
    {
        // The browser says what day it is where the user is, so the file is named for that day.
        api.MapGet("/library/backup", async (string? today, ClaimsPrincipal principal, UserLibraries libraries, TimeProvider clock, CancellationToken ct) =>
        {
            Stream file;
            await using (var lease = await libraries.OpenAsync(principal.UserId()!, ct))
                file = await lease.Library.Get<BackupService>().BackUpForDownloadAsync(ct);
            return Results.File(file, "application/zip", $"Bibliotaph backup {Day(today, clock)}.zip");
        });

        api.MapGet("/library/export/csv", async (string? today, ClaimsPrincipal principal, UserLibraries libraries, TimeProvider clock,
            CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            var library = lease.Library;
            var rows = await library.Get<ExportService>().GetCsvRowsAsync(null, ct);
            var csv = System.Text.Encoding.UTF8.GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(SelectionApi.CsvText(rows, library.Get<UserDrives>()))).ToArray();
            return Results.File(csv, "text/csv", $"Bibliotaph library {Day(today, clock)}.csv");
        });

        // Written as it's read, as a big library's JSON runs to many megabytes; the library stays open until it's done.
        api.MapGet("/library/export/json", (string? today, ClaimsPrincipal principal, UserLibraries libraries, TimeProvider clock) =>
            Results.Stream(async stream =>
            {
                await using var lease = await libraries.OpenAsync(principal.UserId()!, CancellationToken.None);
                await lease.Library.Get<CatalogExport>().WriteJsonAsync(stream, BackupService.AppVersion, clock.GetUtcNow().UtcDateTime);
            }, "application/json", $"Bibliotaph library {Day(today, clock)}.json"));
    }

    /// <summary>A backup to restore: catalog.db, and index.db if it was included, is hundreds of megabytes at most.</summary>
    const long MostBackupBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>
    /// Settings › Backup's weekly backups and Restore: the kept backups to download or restore,
    /// a backup read and previewed before anything changes, and the restore itself.
    /// </summary>
    public static void MapRestoreApi(this RouteGroupBuilder api)
    {
        api.MapGet("/library/backups", async (ClaimsPrincipal principal, IBackupStore store, CancellationToken ct) =>
            (await store.ListAsync(principal.UserId()!, ct)).Select(b => new KeptBackupInfo(b.Name, b.Created, b.Bytes, b.Automatic)).ToList());

        api.MapGet("/library/backups/{name}", async (string name, ClaimsPrincipal principal, IBackupStore store, CancellationToken ct) =>
        {
            if (BackupNames.Created(name) is not { } created || await store.OpenAsync(principal.UserId()!, name, ct) is not { } zip) return Results.NotFound();
            var kind = name.StartsWith(BackupNames.Weekly, StringComparison.Ordinal) ? "weekly backup" : "backup before restoring";
            return Results.File(zip, "application/zip", $"Bibliotaph {kind} {created.UtcDateTime:yyyy-MM-dd}.zip");
        });

        // A kept backup by its name, or a ZIP the browser sends: one made here, or by the Windows app.
        api.MapPost("/library/restore", async (string? backup, HttpContext context, ClaimsPrincipal principal, UserLibraries libraries,
            IBackupStore store, WebBackups backups, CancellationToken ct) =>
        {
            var userId = principal.UserId()!;
            await using var kept = backup is null ? null : BackupNames.Created(backup) is null ? null : await store.OpenAsync(userId, backup, ct);
            if (backup is not null && kept is null) return Results.NotFound();
            if (kept is null)
            {
                if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = MostBackupBytes;
                if (context.Request.ContentLength > MostBackupBytes) return Results.Ok(new RestoreRead(null, "That file is too big to be a Bibliotaph backup."));
            }
            await using var lease = await libraries.OpenAsync(userId, ct);
            var (choice, problem) = await backups.PreviewAsync(lease.Library, kept ?? context.Request.Body, ct);
            return Results.Ok(choice is null ? new RestoreRead(null, problem) : new RestoreRead(Preview(choice, lease.Library.Drives), null));
        });

        api.MapPost("/library/restore/{token}", async (string token, ClaimsPrincipal principal, WebBackups backups, CancellationToken ct) =>
            await backups.RestoreAsync(principal.UserId()!, token, ct) is { } problem
                ? Results.Problem(problem, statusCode: StatusCodes.Status409Conflict)
                : Results.Ok(new ActionResult("Restored the backup. Your books are being read again from their files.")));

        api.MapDelete("/library/restore/{token}", async (string token, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            var held = lease.Library.Get<HeldRestore>();
            if (held.Find(token) is not null)
            {
                lease.Library.Get<BackupService>().DiscardStaging();
                held.Forget();
            }
            return Results.NoContent();
        });
    }

    static RestorePreviewInfo Preview(RestoreChoice choice, UserDrives drives)
    {
        var preview = choice.Preview;
        var manifest = preview.Manifest!;
        var counts = preview.Counts;
        // "1.3.612+4578ab9": the version without the commit it was built from.
        var app = manifest.App.Split('+')[0];
        return new RestorePreviewInfo(choice.Token, new DateTimeOffset(DateTime.SpecifyKind(manifest.CreatedUtc, DateTimeKind.Utc)), app,
            counts?.Books ?? 0, counts?.Collections ?? 0, counts?.Notes ?? 0,
            [.. preview.Folders.Select(f => new RestoreFolderInfo(FolderText.Location(f.Path, drives), choice.Found.Contains(f.Id), f.Files))]);
    }

    static string Day(string? today, TimeProvider clock) =>
        DateOnly.TryParseExact(today, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
