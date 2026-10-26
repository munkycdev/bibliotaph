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
/// /api/settings: Settings › Library's choices below the folders, as the Windows app's Library section has them, on
/// the same services: the start page, the folder names read as labels and the folder rules answered in Needs review.
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

        // Every book under a folder of that name has its hints read again before the answer.
        settings.MapPut("/folder-names", async (FolderNameSwitch change, ClaimsPrincipal principal, UserLibraries libraries, CancellationToken ct) =>
        {
            await using var lease = await libraries.OpenAsync(principal.UserId()!, ct);
            var library = lease.Library;
            if ((await library.Get<VocabularyStore>().GetAsync(ct)).Find(change.Vocabulary, change.Term) is not { } term) return Results.NotFound();
            await library.Get<MetadataHints>().SetFolderLabelEnabledAsync(change.Folder, term, change.Enabled, ct);
            return Results.Ok(await PreferencesAsync(library, ct));
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
        var labels = await library.Get<MetadataHints>().GetFolderLabelsAsync(ct);
        var vocabulary = await library.Get<VocabularyStore>().GetAsync(ct);
        var drives = library.Get<UserDrives>();
        var rules = await library.Get<FolderRules>().GetAnsweredAsync(ct);
        return new LibraryPreferences(start == Library,
            [.. labels.Select(u => new FolderNameLabel(u.Folder, u.Term.Vocabulary, u.Term.Key, Meaning(u.Term), Under(u.Documents), u.Enabled))],
            [.. rules.Select(r => new FolderRuleSwitch(r.Id, FolderRuleText.FolderName(r), FolderRuleText.Describe(r, vocabulary),
                FolderText.Location(FolderRuleText.FullPath(r), drives), r.State == FolderRuleState.On))]);
    }

    /// <summary>"Type: Adventure".</summary>
    static string Meaning(Term term) => $"{MetadataFields.All.FirstOrDefault(f => f.Vocabulary == term.Vocabulary)?.Label ?? term.Vocabulary}: {term.Label}";

    static string Under(int documents) =>
        $"{documents.ToString("N0", CultureInfo.InvariantCulture)} {FolderText.Plural(documents, "document", "documents")} under a folder with this name";
}
