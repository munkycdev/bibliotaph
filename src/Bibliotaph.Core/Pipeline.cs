namespace Bibliotaph.Core;

/// <summary>Which worker lane runs a stage. Lanes pause and run independently.</summary>
public enum Lane
{
    /// <summary>Probe, Text, Covers and rule hints: fast, and needed before a book is searchable.</summary>
    Index,

    /// <summary>OCR of flagged pages: slow, so it never holds up the next book's text.</summary>
    Ocr,
}

/// <summary>The stage versions and order of the processing pipeline.</summary>
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
        _ => 1,
    };

    public static Lane LaneOf(Stage stage) => stage == Stage.Ocr ? Lane.Ocr : Lane.Index;

    /// <summary>The stage queued when a new document is found. Text, Covers and RuleHints follow from Probe; OCR from Text.</summary>
    public static Stage First => Stage.Probe;

    /// <summary>
    /// The stages that read a document's file and only produce derived data, in order: what Reprocess runs again.
    /// Hashing happens before a document has jobs, and classification asks a model, so neither is one of them.
    /// </summary>
    public static IReadOnlyList<Stage> FileStages { get; } = [Stage.Probe, Stage.Text, Stage.Covers, Stage.RuleHints, Stage.Ocr];
}
