using Bibliotaph.Api.Contracts;

namespace Bibliotaph.Web.Services;

/// <summary>The user's settings kept with the library: the start page, the tour, folder rules, Needs review's mode and processing.</summary>
public sealed class SettingsClient(ApiHttp http)
{
    public Task<StartPageChoice> StartPageAsync(CancellationToken ct = default) => http.SendAsync<StartPageChoice>(HttpMethod.Get, "api/settings/start", null, ct);

    public Task SetStartPageAsync(bool library, CancellationToken ct = default) =>
        http.SendAsync(HttpMethod.Put, "api/settings/start", new StartPageChoice(library), ct);

    public Task<TourProgress> TourProgressAsync(CancellationToken ct = default) => http.SendAsync<TourProgress>(HttpMethod.Get, "api/settings/tour", null, ct);

    public Task SetTourProgressAsync(string stage, CancellationToken ct = default) =>
        http.SendAsync(HttpMethod.Put, "api/settings/tour", new TourProgress(stage), ct);

    public Task<LibraryPreferences> LibraryPreferencesAsync(CancellationToken ct = default) =>
        http.SendAsync<LibraryPreferences>(HttpMethod.Get, "api/settings/library", null, ct);

    public Task<LibraryPreferences> SwitchFolderRuleAsync(long id, bool enabled, CancellationToken ct = default) =>
        http.SendAsync<LibraryPreferences>(HttpMethod.Put, $"api/settings/folder-rules/{id}", new RuleSwitch(enabled), ct);

    public Task<ReviewMode> ReviewModeAsync(CancellationToken ct = default) => http.SendAsync<ReviewMode>(HttpMethod.Get, "api/review/mode", null, ct);

    public Task<ReviewMode> SetReviewModeAsync(bool all, CancellationToken ct = default) =>
        http.SendAsync<ReviewMode>(HttpMethod.Put, "api/review/mode", new ReviewMode(all), ct);

    public Task<ProcessingState> ProcessingAsync(CancellationToken ct = default) => http.SendAsync<ProcessingState>(HttpMethod.Get, "api/processing", null, ct);

    public Task<RunAgainDone> RunAgainAsync(string step, CancellationToken ct = default) =>
        http.SendAsync<RunAgainDone>(HttpMethod.Post, $"api/processing/run-again/{Uri.EscapeDataString(step)}", null, ct);

    public Task<ProcessingState> PauseLanesAsync(ProcessingPauses paused, CancellationToken ct = default) =>
        http.SendAsync<ProcessingState>(HttpMethod.Put, "api/processing/paused", paused, ct);
}
