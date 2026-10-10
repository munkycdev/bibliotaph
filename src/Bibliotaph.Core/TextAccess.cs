namespace Bibliotaph.Core;

/// <summary>
/// Why a file's text won't be read by itself, as its card and details say (slice 4i plan, choices 3, 4 and 6). A
/// readable file's text is searchable once its Text stage has run; the others wait on the user, or never come.
/// </summary>
public enum TextAccess
{
    Readable,

    /// <summary>It needs a password Bibliotaph doesn't have: "Locked: open it to unlock".</summary>
    Locked,

    /// <summary>Its protection is a scheme (DRM) Bibliotaph can't open: "Can't be read here".</summary>
    Protected,

    /// <summary>The user chose "Forget its text": its pages, cover and AI results are gone until "Read it again".</summary>
    Forgotten,
}

/// <summary>
/// The reasons the stages stop with for <see cref="TextAccess"/>'s states. They are what Files needing attention shows,
/// and the library tells the states apart by them, so each is matched exactly.
/// </summary>
public static class TextAccessReasons
{
    public const string Locked = "Password required. Open the book to enter it.";

    public const string Protected = "This PDF uses a protection scheme Bibliotaph can't open.";

    public const string Forgotten = "Its text was forgotten. Read it again to search it.";

    /// <summary>What a stage that stopped with <paramref name="status"/> and <paramref name="reason"/> says about the file's text.</summary>
    public static TextAccess Of(StageStatus? status, string? reason) => (status, reason) switch
    {
        (StageStatus.Blocked, Locked) => TextAccess.Locked,
        (StageStatus.Failed, Protected) => TextAccess.Protected,
        (StageStatus.Skipped, Forgotten) => TextAccess.Forgotten,
        _ => TextAccess.Readable,
    };
}
