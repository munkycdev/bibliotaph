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

    /// <summary>A fingerprint of the names the AI was last asked to compare for a vocabulary, and the model asked, so it isn't asked the same again.</summary>
    public static Task<string?> OverlapsAskedAsync(this SettingsStore settings, string vocabulary, CancellationToken ct = default) =>
        settings.GetAsync(AskedKey(vocabulary), ct);

    public static Task SetOverlapsAskedAsync(this SettingsStore settings, string vocabulary, string fingerprint, CancellationToken ct = default) =>
        settings.SetAsync(AskedKey(vocabulary), fingerprint, ct);

    static string AskedKey(string vocabulary) => $"vocabulary.{vocabulary}.overlaps-asked";
}
