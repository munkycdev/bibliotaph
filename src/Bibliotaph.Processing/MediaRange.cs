using System.Globalization;

namespace Bibliotaph.Processing;

/// <summary>
/// One part of a file a video player asks for. A browser plays a video by asking for byte ranges ("bytes=0-", then
/// further on as it seeks or loops), and only plays one whose answer says it may.
/// </summary>
/// <param name="Start">The first byte.</param>
/// <param name="Length">How many bytes.</param>
/// <param name="Total">The whole file's length.</param>
public sealed record MediaRange(long Start, long Length, long Total)
{
    /// <summary>The last byte.</summary>
    public long End => Start + Length - 1;

    /// <summary>The answer's Content-Range header's value.</summary>
    public string ContentRange => string.Create(CultureInfo.InvariantCulture, $"bytes {Start}-{End}/{Total}");

    /// <summary>
    /// The part a Range header asks for in a file of <paramref name="total"/> bytes: a single "bytes=first-last",
    /// "bytes=first-" or "bytes=-suffix", with a last byte past the end cut back to it. Null when the header asks for
    /// nothing in particular, or in a form this doesn't answer, so the whole file is sent; a part that starts past the
    /// end comes back with no bytes, which a server answers 416.
    /// </summary>
    public static MediaRange? Parse(string? header, long total)
    {
        if (string.IsNullOrWhiteSpace(header) || total < 0) return null;
        var value = header.Trim();
        const string unit = "bytes=";
        if (!value.StartsWith(unit, StringComparison.OrdinalIgnoreCase)) return null;
        var spec = value[unit.Length..].Trim();
        if (spec.Contains(',')) return null;
        var dash = spec.IndexOf('-');
        if (dash < 0) return null;
        var first = spec[..dash].Trim();
        var last = spec[(dash + 1)..].Trim();
        if (first.Length == 0)
        {
            if (!long.TryParse(last, NumberStyles.None, CultureInfo.InvariantCulture, out var suffix) || suffix <= 0) return null;
            var length = Math.Min(suffix, total);
            return new MediaRange(total - length, length, total);
        }
        if (!long.TryParse(first, NumberStyles.None, CultureInfo.InvariantCulture, out var start)) return null;
        if (start >= total) return new MediaRange(start, 0, total);
        var end = total - 1;
        if (last.Length > 0)
        {
            if (!long.TryParse(last, NumberStyles.None, CultureInfo.InvariantCulture, out var asked) || asked < start) return null;
            end = Math.Min(asked, end);
        }
        return new MediaRange(start, end - start + 1, total);
    }

    /// <summary>The part of <paramref name="file"/>, a seekable stream, that this range covers; disposing it disposes the file's stream.</summary>
    public Stream Open(Stream file)
    {
        file.Seek(Start, SeekOrigin.Begin);
        return new PartStream(file, Length);
    }

    sealed class PartStream(Stream inner, long length) : Stream
    {
        long _read;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;

        public override long Position
        {
            get => _read;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var read = inner.Read(buffer[..(int)Math.Min(buffer.Length, length - _read)]);
            _read += read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, length - _read)], cancellationToken);
            _read += read;
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
