using Bibliotaph.Core.Metadata;

namespace Bibliotaph.Catalog;

/// <summary>
/// Settings › Vocabulary's choice for each vocabulary the AI can add to (<see cref="AiTerms"/>): whether a new term the
/// AI uses joins it at once, or waits in Needs review for the user to add, file or reject it. Asking is the default.
/// </summary>
public static class AiTermSettings
{
    public static async Task<bool> AddsAiTermsAsync(this SettingsStore settings, string vocabulary, CancellationToken ct = default) =>
        AiTerms.Extends(vocabulary) && await settings.GetAsync(Key(vocabulary), ct) == bool.TrueString;

    public static Task SetAddsAiTermsAsync(this SettingsStore settings, string vocabulary, bool add, CancellationToken ct = default) =>
        settings.SetAsync(Key(vocabulary), add.ToString(), ct);

    static string Key(string vocabulary) => $"vocabulary.{vocabulary}.add-ai-terms";
}
