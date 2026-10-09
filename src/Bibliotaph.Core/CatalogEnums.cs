namespace Bibliotaph.Core;

/// <summary>Whether a source root can be read right now. An offline root never marks its files missing.</summary>
public enum SourceRootAvailability
{
    Online,
    Offline,
    RemovedByUser,
}

public enum FileLocationState
{
    Present,
    Missing,
    /// <summary>A cloud placeholder (OneDrive recall-on-data-access); reading it downloads it.</summary>
    OnlineOnly,
}

public enum ProtectionType
{
    None,
    OpenPassword,
    PermissionsOnly,
    Unsupported,
}

/// <summary>What a library card stands for (catalog entry design, choice 1).</summary>
public enum EntryKind
{
    /// <summary>A book or a single image: one document, or several copies of it.</summary>
    Whole,
    /// <summary>An adventure inside a file: a page range of its parent entry's documents (slice 5).</summary>
    Part,
    /// <summary>Many images shown as one card.</summary>
    Pack,
    /// <summary>Owned with no file: in print or on a virtual tabletop.</summary>
    Elsewhere,
}

/// <summary>Where a metadata value came from. Order is not priority; the effective-value policy decides that.</summary>
public enum AssertionOrigin
{
    Embedded,
    Folder,
    Filename,
    Rule,
    Ai,
    User,
}

public enum AssertionState
{
    Provisional,
    Confirmed,
    Rejected,
    Superseded,
    /// <summary>
    /// Names a vocabulary term that is still pending: held out of the document's metadata until the user adds the term,
    /// maps it to an existing one (and the assertion becomes provisional) or rejects it (and so the assertion).
    /// </summary>
    AwaitingTerm,
}

/// <summary>Where a vocabulary term came from. Starter terms ship with the app; the rest were added later.</summary>
public enum TermOrigin
{
    Starter,
    User,
    /// <summary>Proposed by a classifier; waits in Needs review until the user adds, maps or rejects it.</summary>
    Model,
}

public enum TermState
{
    Active,
    Pending,
    Rejected,
}

/// <summary>Processing stages a document moves through, in pipeline order.</summary>
public enum Stage
{
    Fingerprint,
    Probe,
    Text,
    Covers,
    RuleHints,
    Ocr,
    Classify,
}

public enum StageStatus
{
    Pending,
    Running,
    Complete,
    Partial,
    Blocked,
    Failed,
    Skipped,
}
