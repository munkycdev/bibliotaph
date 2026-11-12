using System.Buffers.Binary;
using System.Text;

namespace Bibliotaph.Processing;

/// <summary>
/// What a WebM file says about its video before its first frame: the picture's size. WebM is Matroska, a tree of EBML
/// elements, each an id, a length and a body; the size is in Segment › Tracks › TrackEntry › Video, which muxers write
/// ahead of the first Cluster so a player can start. Only the start of the file is read, and nothing is decoded, so a
/// video is listed without any codec.
/// </summary>
public static class WebmHeader
{
    /// <summary>How much of the file is read for its header; the tracks are always well inside this.</summary>
    public const int MostBytes = 1 << 20;

    const uint Ebml = 0x1A45DFA3;
    const uint DocType = 0x4282;
    const uint Segment = 0x18538067;
    const uint Tracks = 0x1654AE6B;
    const uint TrackEntry = 0xAE;
    const uint TrackType = 0x83;
    const uint Video = 0xE0;
    const uint PixelWidth = 0xB0;
    const uint PixelHeight = 0xBA;
    const uint Cluster = 0x1F43B675;
    const ulong VideoTrack = 1;

    /// <summary>The first video track's size in pixels, or null when the file isn't a WebM (or Matroska) video.</summary>
    public static (int Width, int Height)? ReadSize(Stream stream)
    {
        var buffer = new byte[MostBytes];
        var length = 0;
        int read;
        while (length < buffer.Length && (read = stream.Read(buffer, length, buffer.Length - length)) > 0) length += read;
        return ReadSize(buffer.AsSpan(0, length));
    }

    /// <inheritdoc cref="ReadSize(Stream)"/>
    public static (int Width, int Height)? ReadSize(ReadOnlySpan<byte> file)
    {
        var at = 0;
        if (!TryElement(file, ref at, out var id, out var size) || id != Ebml || size is not { } headerSize || !IsWebm(Body(file, at, headerSize))) return null;
        at += Body(file, at, headerSize).Length;
        if (!TryElement(file, ref at, out id, out size) || id != Segment) return null;
        // A segment still being written says its length is unknown; its children run to the end of what was read.
        var segment = Body(file, at, size ?? (ulong)(file.Length - at));
        var inSegment = 0;
        while (TryElement(segment, ref inSegment, out id, out size))
        {
            if (id == Cluster || size is not { } length) return null;
            if (id == Tracks) return VideoSize(Body(segment, inSegment, length));
            inSegment += Body(segment, inSegment, length).Length;
        }
        return null;
    }

    static bool IsWebm(ReadOnlySpan<byte> header)
    {
        var at = 0;
        while (TryElement(header, ref at, out var id, out var size) && size is { } length)
        {
            var body = Body(header, at, length);
            if (id == DocType) return Encoding.ASCII.GetString(body).TrimEnd('\0') is "webm" or "matroska";
            at += body.Length;
        }
        return false;
    }

    static (int, int)? VideoSize(ReadOnlySpan<byte> tracks)
    {
        var at = 0;
        while (TryElement(tracks, ref at, out var id, out var size) && size is { } length)
        {
            var entry = Body(tracks, at, length);
            at += entry.Length;
            if (id != TrackEntry) continue;
            ulong? type = null;
            (int, int)? picture = null;
            var inEntry = 0;
            while (TryElement(entry, ref inEntry, out var child, out var childSize) && childSize is { } childLength)
            {
                var body = Body(entry, inEntry, childLength);
                inEntry += body.Length;
                if (child == TrackType) type = Unsigned(body);
                else if (child == Video) picture = Picture(body);
            }
            if (type == VideoTrack && picture is { } found) return found;
        }
        return null;
    }

    static (int, int)? Picture(ReadOnlySpan<byte> video)
    {
        ulong width = 0, height = 0;
        var at = 0;
        while (TryElement(video, ref at, out var id, out var size) && size is { } length)
        {
            var body = Body(video, at, length);
            at += body.Length;
            if (id == PixelWidth) width = Unsigned(body);
            else if (id == PixelHeight) height = Unsigned(body);
        }
        return width is > 0 and <= int.MaxValue && height is > 0 and <= int.MaxValue ? ((int)width, (int)height) : null;
    }

    /// <summary>The body of an element, cut short where the data read ends.</summary>
    static ReadOnlySpan<byte> Body(ReadOnlySpan<byte> data, int at, ulong size) => data.Slice(at, (int)Math.Min(size, (ulong)(data.Length - at)));

    static ulong Unsigned(ReadOnlySpan<byte> body)
    {
        if (body.Length is 0 or > 8) return 0;
        Span<byte> padded = stackalloc byte[8];
        padded.Clear();
        body.CopyTo(padded[(8 - body.Length)..]);
        return BinaryPrimitives.ReadUInt64BigEndian(padded);
    }

    /// <summary>
    /// Reads an element's id and length at <paramref name="at"/>, leaving it at the element's body. A length of null is
    /// EBML's "unknown". False at the end of the data or where it doesn't hold a well-formed element.
    /// </summary>
    static bool TryElement(ReadOnlySpan<byte> data, ref int at, out uint id, out ulong? size)
    {
        id = 0;
        size = null;
        var start = at;
        if (!TryVint(data, ref start, keepMarker: true, out var rawId, out _) || rawId > uint.MaxValue) return false;
        if (!TryVint(data, ref start, keepMarker: false, out var rawSize, out var unknown)) return false;
        id = (uint)rawId;
        size = unknown ? null : rawSize;
        at = start;
        return true;
    }

    /// <summary>
    /// An EBML variable-length integer: its first byte's leading zeros say how many bytes follow. Ids keep the marker
    /// bit, as they're written in the specification; lengths drop it, and all ones means unknown.
    /// </summary>
    static bool TryVint(ReadOnlySpan<byte> data, ref int at, bool keepMarker, out ulong value, out bool unknown)
    {
        value = 0;
        unknown = false;
        if (at >= data.Length || data[at] == 0) return false;
        var length = System.Numerics.BitOperations.LeadingZeroCount((uint)data[at]) - 23;
        if (length > (keepMarker ? 4 : 8) || at + length > data.Length) return false;
        ulong all = keepMarker ? data[at] : (ulong)(data[at] & (0xFF >> length));
        var ones = all == (ulong)(0xFF >> length);
        for (var i = 1; i < length; i++)
        {
            all = (all << 8) | data[at + i];
            ones &= data[at + i] == 0xFF;
        }
        at += length;
        value = all;
        unknown = !keepMarker && ones;
        return true;
    }
}
