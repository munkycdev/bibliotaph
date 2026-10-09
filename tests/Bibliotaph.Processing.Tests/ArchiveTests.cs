using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using Bibliotaph.Catalog;
using Bibliotaph.Core;

namespace Bibliotaph.Processing.Tests;

/// <summary>Reading ZIPs (F3): what a ZIP lists, and the extract cache the PDF engine and the viewer read from.</summary>
public sealed class ArchiveTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("bibliotaph-archives-").FullName;
    readonly ArchiveReader _archives = new(new SourceFileReader());

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    string Zip(string name, params (string Entry, byte[] Bytes)[] members)
    {
        var path = Path.Combine(_dir, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entry, bytes) in members)
        {
            using var stream = zip.CreateEntry(entry).Open();
            stream.Write(bytes);
        }
        return path;
    }

    static byte[] Bytes(string text) => System.Text.Encoding.ASCII.GetBytes(text);

    static string Hex(byte[] bytes) => ContentHash.FromBytes(SHA256.HashData(bytes)).Hex;

    static DocumentSource Member(string zip, string entry, byte[] bytes) =>
        new(1, Hex(bytes), SourceFormats.FromFileName(entry)!, Path.Combine(zip, entry), "", [], zip, entry);

    [Fact]
    public void A_ZIP_lists_its_PDFs_and_images_and_says_why_it_skips_ZIPs_and_passwords()
    {
        var path = Zip("Bundle.zip",
            ("Bundle/", []),
            ("Bundle/Book.PDF", Bytes("%PDF-1.7 book")),
            ("Bundle/Maps/Harbor.jpeg", Bytes("jpeg")),
            ("Bundle/readme.txt", Bytes("text")),
            ("Bundle/Extras.zip", Bytes("zip")),
            ("Bundle/Locked.pdf", Bytes("%PDF secret")));
        SetEncrypted(path, "Bundle/Locked.pdf");

        var members = _archives.List(path);

        Assert.Equal(
            [("Bundle/Book.PDF", (string?)null), ("Bundle/Maps/Harbor.jpeg", null), ("Bundle/Extras.zip", ArchiveReader.Nested), ("Bundle/Locked.pdf", ArchiveReader.Encrypted)],
            members.Select(m => (m.EntryPath, m.Problem)));
        using var zip = ZipFile.OpenRead(path);
        Assert.Equal((13L, zip.GetEntry("Bundle/Book.PDF")!.Crc32), (members[0].SizeBytes, members[0].Crc32));
    }

    [Fact]
    public void A_file_that_isnt_a_ZIP_is_reported_as_damaged()
    {
        var path = Path.Combine(_dir, "Not really.zip");
        File.WriteAllText(path, "This is not a ZIP.");
        Assert.Throws<InvalidDataException>(() => _archives.List(path));
    }

    [Fact]
    public void Member_paths_read_as_Explorer_shows_them_and_never_leave_the_ZIP()
    {
        Assert.Equal(Path.Combine("Bundles", "Set.zip", "Maps", "a.png"), ArchivePaths.MemberPath(Path.Combine("Bundles", "Set.zip"), "Maps/a.png"));
        Assert.Equal(Path.Combine("Set.zip", "etc", "a.png"), ArchivePaths.MemberPath("Set.zip", "/../../etc/./a.png"));
        Assert.Equal("Coriolis Bundle", ArchivePaths.FolderName("Coriolis Bundle.zip"));
        Assert.Equal("Maps", ArchivePaths.FolderName("Maps"));
    }

    [Fact]
    public async Task A_member_is_extracted_once_checked_against_its_hash_and_kept_while_in_use()
    {
        byte[] book = Bytes("%PDF-1.7 book"), map = Bytes("a map, longer than the book");
        var path = Zip("Bundle.zip", ("Book.pdf", book), ("Maps/Map.png", map));
        var paths = new AppPaths(Path.Combine(_dir, "data"));
        // Room for one of them at a time.
        var sources = new SourceFiles(paths, _archives, new DiskSpace(), limitBytes: map.Length);

        await using (var loose = await sources.OpenAsync(new DocumentSource(2, Hex(book), "pdf", path, "", []), Ct))
            Assert.Equal(path, loose.Path);

        var first = await sources.OpenAsync(Member(path, "Book.pdf", book), Ct);
        Assert.Equal(Path.Combine(paths.Extract, Hex(book) + ".pdf"), first.Path);
        Assert.Equal(book, await File.ReadAllBytesAsync(first.Path, Ct));
        await using (var again = await sources.OpenAsync(Member(path, "Book.pdf", book), Ct)) Assert.Equal(first.Path, again.Path);

        // Over the limit, a file in use stays.
        await using (var second = await sources.OpenAsync(Member(path, "Maps/Map.png", map), Ct))
        {
            Assert.True(File.Exists(first.Path));
            await first.DisposeAsync();
        }
        // Released, the least recently used goes when the next one is opened.
        await using (await sources.OpenAsync(Member(path, "Maps/Map.png", map), Ct)) { }
        Assert.False(File.Exists(first.Path));
        Assert.Equal([Hex(map) + ".png"], Directory.GetFiles(paths.Extract).Select(Path.GetFileName));
    }

    [Fact]
    public async Task A_member_that_changed_since_it_was_hashed_is_not_used()
    {
        var path = Zip("Bundle.zip", ("Book.pdf", Bytes("%PDF-1.7 the new printing")));
        var paths = new AppPaths(Path.Combine(_dir, "data"));
        var sources = new SourceFiles(paths, _archives, new DiskSpace());

        await Assert.ThrowsAsync<SourceChangedException>(() => sources.OpenAsync(Member(path, "Book.pdf", Bytes("%PDF-1.7 the old printing")), Ct));
        Assert.Empty(Directory.GetFiles(paths.Extract));

        var gone = Member(path, "Missing.pdf", Bytes("x"));
        await Assert.ThrowsAsync<FileNotFoundException>(() => sources.OpenAsync(gone, Ct));
    }

    /// <summary>Sets the "encrypted" flag on an entry, as a password-protected ZIP has; .NET can't write one.</summary>
    static void SetEncrypted(string path, string entry)
    {
        var bytes = File.ReadAllBytes(path);
        var name = System.Text.Encoding.UTF8.GetBytes(entry);
        for (var i = 0; i + 46 < bytes.Length; i++)
        {
            var signature = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i));
            var (flags, nameLength, nameAt) = signature switch
            {
                0x04034b50 => (i + 6, i + 26, i + 30),
                0x02014b50 => (i + 8, i + 28, i + 46),
                _ => (-1, 0, 0),
            };
            if (flags < 0 || BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(nameLength)) != name.Length || !bytes.AsSpan(nameAt, name.Length).SequenceEqual(name)) continue;
            bytes[flags] |= 1;
        }
        File.WriteAllBytes(path, bytes);
    }
}
