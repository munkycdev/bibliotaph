namespace Bibliotaph.Api.Contracts;

/// <summary>
/// One of Nornis's world events, as POST /api/nornis/v1/worlds/events takes them, up to 100 at a time: a world the user
/// created was created, renamed or deleted, or its files changed. <see cref="OccurredAt"/> orders a world's events.
/// </summary>
public sealed record NornisWorldEvent(Guid EventId, string Kind, Guid WorldId, string WorldName, DateTimeOffset OccurredAt)
{
    public const string Created = "created";
    public const string Renamed = "renamed";
    public const string Deleted = "deleted";
    public const string FilesChanged = "files-changed";

    public const int MostInBatch = 100;
    public const int LongestName = 200;

    /// <summary>Whether it says everything an event must: known kind, IDs, a name of 1 to 200 characters and a time.</summary>
    public bool IsComplete =>
        EventId != Guid.Empty && WorldId != Guid.Empty && Kind is Created or Renamed or Deleted or FilesChanged
        && WorldName is { Length: > 0 and <= LongestName } && !string.IsNullOrWhiteSpace(WorldName) && OccurredAt != default;
}
