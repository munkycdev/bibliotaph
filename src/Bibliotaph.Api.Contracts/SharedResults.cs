namespace Bibliotaph.Api.Contracts;

/// <summary>
/// What one of the user's devices worked out about a file, shared through the web library by the file's content
/// (its SHA-256), so the user's other devices with the same file use it instead of working it out again: the AI's
/// reading of the book as a whole, or of its chapters. <paramref name="Id"/> is the result's own on the device that made
/// it; <paramref name="Content"/> is the result as JSON, which the web library keeps as it is.
/// </summary>
public sealed record SharedResult(string ContentHash, string Kind, int PromptVersion, string Id, DateTime FinishedUtc, string Content);

/// <summary>
/// Results the web library holds, after the place a device asked from: the device's own aren't among them.
/// <paramref name="Last"/> is where to ask from next; <paramref name="More"/> says there are more to ask for now.
/// </summary>
public sealed record SharedResultPage(IReadOnlyList<SharedResult> Results, long Last, bool More);

/// <summary>Results a device sends to the web library.</summary>
public sealed record SharedResultUpload(IReadOnlyList<SharedResult> Results);

/// <summary>How many of the results sent the web library kept: those it held already, or an older one of, are left out.</summary>
public sealed record SharedResultsKept(int Kept);
