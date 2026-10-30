namespace Bibliotaph.Api.Contracts;

/// <summary>
/// Settings › Processing, as the Windows app's: the counts behind each step's bar, what each part of indexing is on
/// now, and which lanes are paused. The browser works out the time left from how the counts move between polls, as the
/// Windows app does between ticks. Looking books up online is counted like the AI: why it waits, the books done and
/// still to do, and where they're looked up (<see cref="LookupCatalogues"/>, "Open Library or Google Books"). Scanning
/// whole books is the AI's slow last step, with its own switch in Settings › AI (<see cref="ScanWholeBooks"/>). A
/// connected PC whose own AI is cataloguing says so (<see cref="Pcs"/>): the work it does reaches the web library as
/// its results arrive, so the page can say where it is being done. <see cref="RunAgain"/> are the steps that can be run
/// again for every book, each with the button at the end of its row.
/// </summary>
public sealed record ProcessingState(
    string Summary, bool HasFolders, bool Scanning, long Files, long ToRead, long Indexed, long Queued, long OcrPagesDone, long OcrPages,
    bool AiOn, string? AiWaiting, long Classified, long ToClassify, ProcessingPauses Paused, IReadOnlyList<string> Now, long Attention,
    string? LookupWaiting = null, long LookedUp = 0, long ToLookUp = 0, string? LookupCatalogues = null,
    long ScannedWhole = 0, long ToScanWhole = 0, bool ScanWholeBooks = true, IReadOnlyList<PcProcessing>? Pcs = null,
    IReadOnlyList<RunAgainOffer>? RunAgain = null);

/// <summary>
/// A step of Settings › Processing that can be run again for every book: its key (<c>finding</c>, <c>lookup</c>,
/// <c>ai</c> or <c>scan</c>), the button's tooltip, and the question asked first with its answer's label; a step that
/// costs nothing to run again (looking through the folders) asks nothing.
/// </summary>
public sealed record RunAgainOffer(string Step, string Hint, string? Question = null, string? Confirm = null);

/// <summary>What running a step again did, in a line.</summary>
public sealed record RunAgainDone(string Note);

/// <summary>Which lanes the user paused: reading files and making them searchable, reading scans, and the AI.</summary>
public sealed record ProcessingPauses(bool Indexing, bool Scans, bool Ai);

/// <summary>
/// A connected PC's AI at work, as the web shows it: the PC's name, what it is on now in the Windows app's words
/// ("Asking the AI about The Sunken Lantern"), whether its AI is paused or waiting and why, and its own counts, which
/// take in the books only that PC has. <paramref name="Step"/> is the stage it is running, if any (<see cref="PcActivity"/>).
/// </summary>
public sealed record PcProcessing(string Name, string? Step, string? Doing, bool Paused, string? Waiting,
    long Classified, long ToClassify, long ScannedWhole, long ToScanWhole);

/// <summary>
/// What a connected PC's AI is doing, as the PC sends it: the stage it is running ("Classify" or "ChapterScan", or none
/// between books), the book's content hash, never its path or name, so the web names it only when the web library has
/// it too; whether the lane is paused or waiting, and why; and the PC's own counts.
/// </summary>
public sealed record PcActivity(string? Step, string? ContentHash, bool Paused, string? Waiting,
    long Classified, long ToClassify, long ScannedWhole, long ToScanWhole);
