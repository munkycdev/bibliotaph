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
