using System.IO.Compression;
using Bibliotaph.Catalog;
using Bibliotaph.Core;

namespace Bibliotaph.Processing;

/// <summary>
/// Reads ZIPs in a library folder through the read-only reader (F3): lists the PDFs and images inside and opens one for
/// reading. Nothing is ever written to a ZIP or beside it.
/// </summary>
public sealed class ArchiveReader(ISourceFileReader reader)
{
    /// <summary>A member bigger than this isn't extracted: the extract cache could never hold it.</summary>
    public const long MaxMemberBytes = SourceFiles.DefaultLimitBytes;

    public const string Nested = "A ZIP inside a ZIP isn't read. Unpack it to include its files.";
    public const string Encrypted = "It is password-protected inside the ZIP.";
    public const string TooBig = "It is too big to read from inside a ZIP (over 5 GB). Unpack it to include it.";
    public const string Damaged = "It is damaged inside the ZIP.";

    /// <summary>
    /// The PDFs and images in a ZIP, and the ZIPs inside it, which are listed with the reason they aren't read. Other
    /// files and folders are left out. Throws <see cref="InvalidDataException"/> for a file that isn't a readable ZIP.
    /// </summary>
    public IReadOnlyList<ArchiveMember> List(string archivePath)
    {
        using var zip = Open(archivePath);
        var members = new List<ArchiveMember>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\') || entry.Name.Length == 0) continue;
            var nested = SourceFormats.IsArchive(entry.Name);
            if (!nested && SourceFormats.FromFileName(entry.Name) is null) continue;
            // A ZIP can hold the same name twice; only the first is read, as Explorer shows only one.
            if (!seen.Add(entry.FullName)) continue;
            var problem = nested ? Nested
                : entry.IsEncrypted ? Encrypted
                : entry.Length > MaxMemberBytes ? TooBig
                : null;
            members.Add(new ArchiveMember(entry.FullName, entry.Length, entry.LastWriteTime.UtcDateTime, entry.Crc32, problem));
        }
        return members;
    }

    /// <summary>Opens a ZIP for reading its members one after another.</summary>
    public ZipArchive Open(string archivePath) => new(reader.OpenRead(archivePath), ZipArchiveMode.Read, leaveOpen: false);

    /// <summary>
    /// A member's bytes as they decompress. It can't run past the size the ZIP states for it, which stops a crafted ZIP
    /// from filling the disk. Throws <see cref="FileNotFoundException"/> when the ZIP no longer holds it.
    /// </summary>
    public static Stream OpenMember(ZipArchive zip, string entryPath)
    {
        var entry = zip.GetEntry(entryPath) ?? throw new FileNotFoundException("The file is no longer in its ZIP.", entryPath);
        return new BoundedStream(entry.Open(), entry.Length);
    }

    /// <summary>Reads a member, failing as damaged once it gives more bytes than the ZIP says it holds.</summary>
    sealed class BoundedStream(Stream inner, long limit) : Stream
    {
        long _read;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => limit;

        public override long Position
        {
            get => _read;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

        public override int Read(Span<byte> buffer) => Count(inner.Read(buffer));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Count(await inner.ReadAsync(buffer, cancellationToken));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        int Count(int read)
        {
            _read += read;
            if (_read > limit) throw new InvalidDataException("The file inside the ZIP is bigger than the ZIP says.");
            return read;
        }

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
