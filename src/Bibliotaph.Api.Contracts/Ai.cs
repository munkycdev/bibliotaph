namespace Bibliotaph.Api.Contracts;

/// <summary>
/// Settings › AI: the model service saved, with only the end of its key, whether AI is on and how far it has got,
/// Reclassify when an earlier model or a failure leaves books to read again, the folders sent to AI, and whether books
/// are scanned whole, with how far that has got. With an embedding model, whether the whole library is read for meaning
/// and how far that has got (<see cref="LibraryProgress"/>, null without one).
/// </summary>
public sealed record AiPage(bool Enabled, string? Endpoint, string? Model, bool RemoteAllowed, string? KeyEnding, string Progress,
    string? Waiting, AiReclassify? Reclassify, IReadOnlyList<AiFolder> Folders, bool ScanWholeBooks, string ScanProgress, string? EmbeddingModel,
    bool ReadLibrary = false, string? LibraryProgress = null);

public sealed record AiReclassify(string Text, string Label, bool HasFailed);

public sealed record AiFolder(long Id, string Name, string Location, bool Send);

/// <summary>Connect: the address typed and, for a service that lists its models only with a key, the key typed.</summary>
public sealed record AiConnect(string Endpoint, string? Key);

/// <summary>What Connect found, or why it found nothing.</summary>
public sealed record AiConnection(string? Endpoint, string? Text, string? Problem, IReadOnlyList<string> Models);

/// <summary>
/// Save: the setup on the page. A blank key keeps the one saved for the address; a blank embedding model finds Ask's
/// passages by their words alone.
/// </summary>
public sealed record AiSave(string Endpoint, string Model, string? Key, bool RemoteAllowed, string? EmbeddingModel = null);

/// <summary>What a change said: a note, or a problem that stopped it.</summary>
public sealed record AiNote(string? Note, string? Problem);

public sealed record AiSwitch(bool On);

public sealed record AiFolderSend(bool Send);

/// <summary>Test with a book, for the address and model on the page, with the key saved for it.</summary>
public sealed record AiTest(string Endpoint, string Model, bool RemoteAllowed);

/// <summary>A test's result: the book it read and what it kept, each with page and quote, and what the check threw out.</summary>
public sealed record AiTestReport(string? Summary, string? Problem, IReadOnlyList<AiTestReportLine> Kept, IReadOnlyList<AiTestReportLine> Dropped);

public sealed record AiTestReportLine(string Text, string Detail);

/// <summary>
/// AI reading, GET /api/ai/reading: the book the AI is reading now, or why none is (<paramref name="Idle"/>), and the
/// last books read with what the evidence check kept and dropped.
/// </summary>
public sealed record AiReadingPage(AiReadingNow? Now, string? Idle, IReadOnlyList<AiReadingBookRead> Recent);

/// <summary>
/// The book being read: what the AI is doing, how long it has been at it, how much of the excerpt it has read when the
/// model service says (0 to 100), and its answer's fields as it writes them.
/// </summary>
public sealed record AiReadingNow(string Title, string Step, double Seconds, double? ReadPercent, IReadOnlyList<AiTestReportLine> Fields);

/// <summary>A book read: "Kept 5 values, dropped 2 · 41 s", what was kept and thrown away, or why it couldn't be read.</summary>
public sealed record AiReadingBookRead(string Title, string Summary, string? Problem, IReadOnlyList<AiTestReportLine> Kept,
    IReadOnlyList<AiTestReportLine> Dropped);
