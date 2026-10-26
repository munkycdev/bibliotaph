using System.Net;

namespace Bibliotaph.Storage;

/// <summary>A file or folder in connected storage. <see cref="Size"/> means nothing for a folder: some providers give its total, some 0.</summary>
public sealed record StorageEntry(string Name, bool IsFolder, long Size, DateTime ModifiedUtc);

/// <summary>Bytes <see cref="From"/> to <see cref="To"/> of a file, both included, as an HTTP Range header names them.</summary>
public readonly record struct ByteRange(long From, long To)
{
    public long Length => To - From + 1;
}

/// <summary>
/// The provider answered with an error Bibliotaph can't work around, such as a folder that's gone or access withdrawn.
/// An IOException, so indexing treats a book it couldn't download like a file it couldn't open.
/// </summary>
public sealed class StorageException(HttpStatusCode status, string message) : IOException(message)
{
    public HttpStatusCode Status { get; } = status;
}

/// <summary>
/// One connected storage account, as a library reads it: folders by path, slash-separated and empty for the top. Each
/// provider's client sits behind this, so listing, indexing and the reader never ask which provider a book is on.
/// </summary>
public interface IStorageDrive
{
    /// <summary>The provider's name, for messages: "OneDrive".</summary>
    string ProviderName { get; }

    /// <summary>
    /// Whether <see cref="ReadLinkAsync"/> gives links a page on another site may read. Without them the reader reads
    /// through the server.
    /// </summary>
    bool HasReadLinks { get; }

    /// <summary>One file or folder, or null when there's nothing at that path.</summary>
    Task<StorageEntry?> ItemAsync(string path, CancellationToken ct = default);

    /// <summary>A folder's contents, every page of them, or null when the folder doesn't exist (or is a file).</summary>
    Task<IReadOnlyList<StorageEntry>?> ChildrenAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// A file's bytes, streamed: all of them, or only <paramref name="range"/>, for the reader when the provider has no
    /// link a browser can read from. Throws <see cref="StorageException"/> with NotFound when it isn't there.
    /// </summary>
    Task<Stream> OpenReadAsync(string path, ByteRange? range = null, CancellationToken ct = default);

    /// <summary>
    /// A short-lived link a browser can fetch the file's byte ranges from without a token, or null when there's nothing
    /// at that path. Only asked of a drive that <see cref="HasReadLinks"/>.
    /// </summary>
    Task<string?> ReadLinkAsync(string path, CancellationToken ct = default);
}

/// <summary>
/// A drive the server asks now and then whether anything changed, where the provider has no notifications to send. The
/// cursor is the provider's own: a change log's position, or a folder's ETag.
/// </summary>
public interface IStorageChangeLog
{
    /// <summary>Where the drive stands now, to ask later what changed since.</summary>
    Task<string> StartAsync(CancellationToken ct = default);

    /// <summary>Whether anything changed since <paramref name="cursor"/>, and the cursor to ask from next time.</summary>
    Task<(bool Changed, string Next)> ChangesSinceAsync(string cursor, CancellationToken ct = default);
}

/// <summary>The storage one library can reach: what its owner has connected.</summary>
public interface IStorageDrives
{
    /// <returns>The account's drive, or null when it isn't connected (any more).</returns>
    IStorageDrive? Find(string provider, string account);
}

public static class StorageDrives
{
    /// <summary>The drive a library path is on and the path inside it, or null when it isn't on connected storage.</summary>
    public static (IStorageDrive Drive, StorageLocation Location)? Locate(this IStorageDrives drives, string path) =>
        StoragePath.TryParse(path, out var location) && drives.Find(location.Provider, location.Account) is { } drive ? (drive, location) : null;
}

/// <summary>What providers' download answers share.</summary>
public static class StorageDownloads
{
    /// <summary>
    /// The body of a download asked for with a Range header. A server that ignored the header sent the whole file, so
    /// the bytes before the range are skipped and the read stops at its end.
    /// </summary>
    public static async Task<Stream> BodyAsync(HttpResponseMessage response, ByteRange? range, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStreamAsync(ct);
        if (range is not { } wanted || response.StatusCode == HttpStatusCode.PartialContent) return body;
        var skip = new byte[81920];
        for (var left = wanted.From; left > 0;)
        {
            var read = await body.ReadAsync(skip.AsMemory(0, (int)Math.Min(skip.Length, left)), ct);
            if (read == 0) break;
            left -= read;
        }
        return new LimitedStream(body, wanted.Length);
    }

    sealed class LimitedStream(Stream inner, long length) : Stream
    {
        long _left = length;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_left <= 0) return 0;
            var read = inner.Read(buffer[..(int)Math.Min(buffer.Length, _left)]);
            _left -= read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_left <= 0) return 0;
            var read = await inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, _left)], cancellationToken);
            _left -= read;
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
