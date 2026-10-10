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
    /// <summary>
    /// A value the user set on another card of the same book, for a field with one value, that disagreed with this
    /// card's when the two were joined as copies (F2). Held out of the metadata, and offered by a "Copies disagree"
    /// card in Needs review until the user decides the field.
    /// </summary>
    SetAside,
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

    /// <summary>Fingerprints a document's pages from its stored text and joins it to a copy of the same book (F2).</summary>
    Match,
}

/// <summary>What the user said about a pair of files that Match compared.</summary>
public enum CopyAnswer
{
    /// <summary>"Not the same book": the two stay on separate cards.</summary>
    NotSameBook,
}

/// <summary>What happens to the images in a folder or ZIP (F4 plan, choices 2 to 4).</summary>
public enum PackAnswer
{
    /// <summary>Its images are one pack card, and images added later join it.</summary>
    Packed,
    /// <summary>"Split into separate images": each image has its own card, and the folder isn't packed again.</summary>
    Split,
}

/// <summary>Why Match thinks one file may be a new version of another (F2 plan, choice 4).</summary>
public enum VersionEvidence
{
    /// <summary>Most of the smaller file's pages have the same text as pages of the other.</summary>
    SharedPages,

    /// <summary>Both cards have the same title and publisher.</summary>
    TitleAndPublisher,
}

/// <summary>The user's answer to a "new version?" card in Needs review.</summary>
public enum VersionAnswer
{
    /// <summary>Join the book's card and open this file from now on.</summary>
    MakeCurrent,

    /// <summary>Join the book's card as another copy; the card keeps opening the file it opened.</summary>
    KeepAsCopy,

    /// <summary>A different book: keep the two cards apart and don't ask again.</summary>
    SeparateBook,
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
