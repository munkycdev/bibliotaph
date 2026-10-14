namespace Bibliotaph.Core;

/// <summary>Which worker lane runs a stage. Lanes pause and run independently.</summary>
public enum Lane
{
    /// <summary>Probe, Text, Covers and rule hints: fast, and needed before a book is searchable.</summary>
    Index,

    /// <summary>OCR of flagged pages: slow, so it never holds up the next book's text.</summary>
    Ocr,

    /// <summary>Classification by the AI: one book at a time, and only while AI is set up and switched on.</summary>
    Classify,
}

/// <summary>
/// What the job queue in index.db needs to know about the processing pipeline, whichever stages an app runs: stage
/// versions, which stages wait behind everything else, the first stage, and the stages Reprocess runs again. The rest
/// of what a stage is, its lane and what follows it, each stage says about itself.
/// </summary>
public static class Pipeline
{
    /// <summary>
    /// Bumping a stage's version re-runs that stage, and only that stage, for every document,
    /// because jobs are unique by content hash, stage and stage version.
    /// </summary>
    public static int Version(Stage stage) => stage switch
    {
        Stage.Probe => 1,
        Stage.Text => 1,
        Stage.Covers => 1,
        Stage.RuleHints => 1,
        Stage.Ocr => 1,
        // Never bumped for a new prompt or model: those are recorded per run, and reclassifying is the user's call.
        Stage.Classify => 1,
        // 2: new-version cards, so a library matched before gets them.
        Stage.Match => 2,
        // Never bumped for a new prompt: a finished scan is kept until the file changes or the user asks again.
        Stage.ChapterScan => 1,
        _ => 1,
    };

    /// <summary>
    /// The priority every chapter-scan job is queued at, below the default of 0, so a book waiting to be classified is
    /// always leased first: reading whole books is a long, optional pass that should never hold anything up.
    /// </summary>
    public const int BackgroundPriority = -100;

    /// <summary>Whether a stage is a long, optional pass that runs only when nothing else in its lane is waiting.</summary>
    public static bool IsBackground(Stage stage) => stage == Stage.ChapterScan;

    /// <summary>
    /// The stage queued when a new document is found. Text, Covers and RuleHints follow from Probe; OCR, Classify and
    /// Match from Text, and Classify and Match wait for the book's OCR to finish.
    /// </summary>
    public static Stage First => Stage.Probe;

    /// <summary>
    /// The stages that read a document's file and only produce derived data, in order: what Reprocess runs again.
    /// Hashing happens before a document has jobs, and classification asks a model, so neither is one of them.
    /// </summary>
    public static IReadOnlyList<Stage> FileStages { get; } = [Stage.Probe, Stage.Text, Stage.Covers, Stage.RuleHints, Stage.Ocr];
}
