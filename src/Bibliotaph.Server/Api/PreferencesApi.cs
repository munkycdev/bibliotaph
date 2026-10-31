using System.Globalization;
using System.Security.Claims;
using Bibliotaph.Api.Contracts;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Bibliotaph.Processing;
using Bibliotaph.Server.Auth;
using Bibliotaph.Server.Libraries;
using Bibliotaph.Server.Storage;

namespace Bibliotaph.Server.Api;

/// <summary>
/// /api/settings: the choices in Settings › Library below the folders and in Preferences, on
/// the same services: the start page and the folder rules answered in Needs review.
/// </summary>
public static class PreferencesApi
{
    const string Home = "Home", Library = "Library";

    public static void MapPreferencesApi(this RouteGroupBuilder api)
    {
        var settings = api.MapGroup("/settings");

        settings.MapGet("/library", async (ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            return await PreferencesAsync(lease.Library, ct);
        });

        settings.MapGet("/start", async (ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            return new StartPageChoice(await lease.Library.Get<SettingsStore>().GetAsync(SettingKeys.StartPage, ct) == Library);
        });

        // The Windows app keeps the same setting by its page's name.
        settings.MapPut("/start", async (StartPageChoice choice, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            await lease.Library.Get<SettingsStore>().SetAsync(SettingKeys.StartPage, choice.Library ? Library : Home, ct);
            return Results.NoContent();
        });

        settings.MapGet("/tour", async (ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            return new TourProgress(await lease.Library.Get<SettingsStore>().GetAsync(SettingKeys.TourStage, ct));
        });

        // The tour only ever goes forward: its first chapter seen, then the whole of it seen or skipped.
        settings.MapPut("/tour", async (TourProgress progress, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            if (progress.Stage is not (Tour.StageFolders or Tour.StageBooks)) return Results.BadRequest();
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            await lease.Library.Get<SettingsStore>().SetAsync(SettingKeys.TourStage, progress.Stage, ct);
            return Results.NoContent();
        });

        // Switching a rule on switches off another there for the same one-value field, so the whole list comes back.
        settings.MapPut("/folder-rules/{id:long}", async (long id, RuleSwitch change, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            var library = lease.Library;
            var rules = library.Get<FolderRules>();
            if ((await rules.GetAnsweredAsync(ct)).FirstOrDefault(r => r.Id == id) is not { } rule) return Results.NotFound();
            await rules.SetAsync(rule, change.Enabled, ct);
            return Results.Ok(await PreferencesAsync(library, ct));
        });
    }

    static async Task<LibraryPreferences> PreferencesAsync(UserLibrary library, CancellationToken ct)
    {
        var start = await library.Get<SettingsStore>().GetAsync(SettingKeys.StartPage, ct);
        var vocabulary = await library.Get<VocabularyStore>().GetAsync(ct);
        var drives = library.Get<UserDrives>();
        var rules = await library.Get<FolderRules>().GetAnsweredAsync(ct);
        return new LibraryPreferences(start == Library,
            [.. rules.Select(r => new FolderRuleSwitch(r.Id, FolderRuleText.FolderName(r), FolderRuleText.Describe(r, vocabulary),
                FolderText.Location(FolderRuleText.FullPath(r), drives), r.State == FolderRuleState.On))]);
    }
}
