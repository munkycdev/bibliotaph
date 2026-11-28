namespace Bibliotaph.Api.Contracts;

/// <summary>
/// What the player plays for a card: an album's tracks in order, playing from <see cref="Start"/>, or a single track.
/// <see cref="Title"/> is the album's, null for a track of its own.
/// </summary>
public sealed record PlayerTracks(long EntryId, string? Title, int Start, IReadOnlyList<PlayerTrack> Tracks);

/// <summary>
/// A track as the player lists and plays it: its title and artist from its tags, else its file's name; what it is, to
/// read its bytes through the same cache as a book's (<see cref="ContentHash"/> and <see cref="Size"/>, with the
/// document's link); and how long it plays. <see cref="Cover"/> is a name for /api/library/covers/. <see cref="Problem"/>
/// says why it can't be played here, as a file inside a ZIP.
/// </summary>
public sealed record PlayerTrack(long DocumentId, string Title, string? Artist, string Format, string MediaType, string ContentHash, long Size,
    long? DurationMs, string? Cover, string? Problem = null);
