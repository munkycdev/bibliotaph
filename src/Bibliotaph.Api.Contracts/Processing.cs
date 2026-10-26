namespace Bibliotaph.Api.Contracts;

/// <summary>
/// Settings › Processing, as the Windows app's: the counts behind each step's bar, what each part of indexing is on
/// now, and which lanes are paused. The browser works out the time left from how the counts move between polls, as the
/// Windows app does between ticks.
/// </summary>
public sealed record ProcessingState(
    string Summary, bool HasFolders, bool Scanning, long Files, long ToRead, long Indexed, long Queued, long OcrPagesDone, long OcrPages,
    bool AiOn, string? AiWaiting, long Classified, long ToClassify, ProcessingPauses Paused, IReadOnlyList<string> Now, long Attention);

/// <summary>Which lanes the user paused: reading files and making them searchable, reading scans, and the AI.</summary>
public sealed record ProcessingPauses(bool Indexing, bool Scans, bool Ai);
