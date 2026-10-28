namespace Bibliotaph.Api.Contracts;

/// <summary>
/// Settings › Processing, as the Windows app's: the counts behind each step's bar, what each part of indexing is on
/// now, and which lanes are paused. The browser works out the time left from how the counts move between polls, as the
/// Windows app does between ticks. Looking books up online is counted like the AI: whether it is on, why it waits, and
/// the books done and still to do.
/// </summary>
public sealed record ProcessingState(
    string Summary, bool HasFolders, bool Scanning, long Files, long ToRead, long Indexed, long Queued, long OcrPagesDone, long OcrPages,
    bool AiOn, string? AiWaiting, long Classified, long ToClassify, ProcessingPauses Paused, IReadOnlyList<string> Now, long Attention,
    bool LookupsOn = false, string? LookupWaiting = null, long LookedUp = 0, long ToLookUp = 0);

/// <summary>Which lanes the user paused: reading files and making them searchable, reading scans, and the AI.</summary>
public sealed record ProcessingPauses(bool Indexing, bool Scans, bool Ai);

/// <summary>Whether books are looked up online by the ISBNs they print, and where: "Open Library or Google Books".</summary>
public sealed record LookupSetting(bool On, string? Sources = null);
