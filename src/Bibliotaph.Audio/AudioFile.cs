using Bibliotaph.Core;

namespace Bibliotaph.Audio;

/// <summary>
/// What an audio file says about itself: its tags (any of which may be missing) and how it plays. <see
/// cref="Duration"/> is read from the file's headers, never by decoding it.
/// </summary>
public sealed record AudioDetails(
    string? Title, string? Artist, string? AlbumArtist, string? Album, int? Track, int? Disc, int? Year, string? Genre,
    string? Publisher, string? Comment, TimeSpan Duration, int BitrateKbps, int SampleRate, string Codec)
{
    /// <summary>Who made it, as a card credits it: the track's artist, else the album's.</summary>
    public string? Credit => Artist ?? AlbumArtist;
}

/// <summary>
/// Reads audio files (MP3, M4A, FLAC, WAV, Ogg Vorbis, Opus) through ATL.NET, only ever from a stream it is given. The
/// library can write tags too; those calls are banned in this project, and nothing here keeps a file open.
/// </summary>
public static class AudioFile
{
    /// <summary>Artwork bigger than this isn't drawn as a cover: no real album's is, and a crafted tag shouldn't cost memory.</summary>
    public const int MostArtworkBytes = 16 * 1024 * 1024;

    static AudioFile()
    {
        // The library's defaults suit a tag editor: a title made up from the file name, and every frame kept. A missing
        // title is left missing here, so the card's file-name title shows instead and reads as one.
        ATL.Settings.UseFileNameWhenNoTitle = false;
        ATL.Settings.ReadAllMetaFrames = false;
        ATL.Settings.OutputStacktracesToConsole = false;
    }

    /// <summary>The media type a player and the library take for a format, or null when it isn't audio.</summary>
    public static string? MediaType(string format) => format switch
    {
        SourceFormats.Mp3 => "audio/mpeg",
        SourceFormats.M4a => "audio/mp4",
        SourceFormats.Flac => "audio/flac",
        SourceFormats.Wav => "audio/wav",
        SourceFormats.Ogg => "audio/ogg",
        SourceFormats.Opus => "audio/ogg; codecs=opus",
        _ => null,
    };

    /// <summary>The file's tags and how long it plays, or null when it isn't audio the library can read.</summary>
    public static AudioDetails? Read(Stream stream, string format)
    {
        if (Open(stream, format) is not { } track) return null;
        return new AudioDetails(
            Clean(track.Title), Clean(track.Artist), Clean(track.AlbumArtist), Clean(track.Album), Positive(track.TrackNumber), Positive(track.DiscNumber),
            track.Year is > 0 and < 10000 ? track.Year : null, Clean(track.Genre), Clean(track.Publisher), Clean(track.Comment),
            TimeSpan.FromMilliseconds(Math.Max(0, track.DurationMs)), Math.Max(0, track.Bitrate), (int)Math.Max(0, track.SampleRate),
            track.AudioFormat.DataFormat?.Name is { Length: > 0 } codec ? codec : track.AudioFormat.Name);
    }

    /// <summary>
    /// The artwork the file carries, as its bytes: the front cover if it says which is, else its first picture. Null when
    /// it has none, or none small enough to be a cover.
    /// </summary>
    public static byte[]? ReadArtwork(Stream stream, string format)
    {
        if (Open(stream, format) is not { } track) return null;
        var pictures = track.EmbeddedPictures.Where(p => p.PictureData is { Length: > 0 and <= MostArtworkBytes }).ToList();
        return (pictures.FirstOrDefault(p => p.PicType == ATL.PictureInfo.PIC_TYPE.Front) ?? pictures.FirstOrDefault())?.PictureData;
    }

    static ATL.Track? Open(Stream stream, string format)
    {
        if (MediaType(format) is not { } type) return null;
        ATL.Track track;
        try
        {
            track = new ATL.Track(stream, type);
        }
        catch (Exception ex) when (ex is EndOfStreamException or InvalidDataException or ArgumentException or InvalidOperationException
                                       or IndexOutOfRangeException or OverflowException or NotSupportedException or FormatException)
        {
            // A damaged or hostile file: the library's parsers throw what their reading ran into. Other IO failures (the
            // file in use, a drive gone) pass on, so the stage tries again later.
            return null;
        }
        // What the library calls a file it couldn't tell the format of, or one with no audio in it.
        return track.AudioFormat.MainID == 0 || track.DurationMs <= 0 ? null : track;
    }

    static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().Replace('\0', ' ');

    static int? Positive(int? value) => value is > 0 ? value : null;
}
