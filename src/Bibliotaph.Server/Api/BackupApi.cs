using System.Globalization;
using System.Security.Claims;
using Bibliotaph.Catalog;
using Bibliotaph.Processing;
using Bibliotaph.Server.Auth;
using Bibliotaph.Server.Libraries;
using Bibliotaph.Server.Storage;

namespace Bibliotaph.Server.Api;

/// <summary>
/// /api/library/backup and /api/library/export: Settings › Backup's downloads, the same files the Windows app's Back up
/// now, Export CSV and Export JSON save. A backup restores in the Windows app, its folders then found again or left
/// offline, so a user can always take their work away.
/// </summary>
public static class BackupApi
{
    public static void MapBackupApi(this RouteGroupBuilder api)
    {
        // The browser says what day it is where the user is, so the file is named as the Windows app would name it there.
        api.MapGet("/library/backup", async (string? today, ClaimsPrincipal principal, UserLibraries libraries, ServerPaths paths, TimeProvider clock,
            CancellationToken ct) =>
        {
            Stream file;
            await using (var lease = await libraries.OpenAsync(principal.UserId()!, ct))
                file = await lease.Library.Get<BackupService>().BackUpForDownloadAsync(paths.Downloads, ct);
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

    static string Day(string? today, TimeProvider clock) =>
        DateOnly.TryParseExact(today, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
