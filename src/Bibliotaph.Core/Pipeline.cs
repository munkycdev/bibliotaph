namespace Bibliotaph.Core;

/// <summary>Which worker lane runs a stage. Lanes pause and run independently.</summary>
public enum Lane
{
    /// <summary>Probe, Text and Covers: fast, and needed before a book is searchable.</summary>
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
        Stage.Ocr => 1,
        _ => 1,
    };

    public static Lane LaneOf(Stage stage) => stage == Stage.Ocr ? Lane.Ocr : Lane.Index;

    /// <summary>The stages queued when a new document is found. Text and Covers follow from Probe; OCR from Text.</summary>
    public static Stage First => Stage.Probe;
}
