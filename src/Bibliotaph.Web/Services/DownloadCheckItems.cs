using Bibliotaph.Core;

namespace Bibliotaph.Web.Services;

/// <summary>A file the browser picked, as check.js lists it: its place in the folder picked, or a ZIP's name.</summary>
public sealed record PickedFile(int Id, string Path, long Size);

/// <summary>A file in a picked ZIP, as check.js reads its directory.</summary>
public sealed record ZipMember(string Name, long Size, bool Encrypted, bool Readable);

/// <summary>A PDF's pages as check.js read them, or why it couldn't ("password", "invalid").</summary>
public sealed record PickedPages(string? Unread, IReadOnlyList<string?>? Texts, string? Title, string? Author, string? Subject, string? Keywords);

/// <summary>
/// A file to check: a picked file, or a member (<see cref="Entry"/>) of a picked ZIP. <see cref="Location"/> is where
/// it is for the results, and <see cref="RelativePath"/> where it is below the folder picked, which folder names are
/// read from; <see cref="Problem"/> says why it can't be read.
/// </summary>
public sealed record CheckTarget(int Id, string? Entry, string Location, string RelativePath, string? Problem = null)
{
    public string Name => System.IO.Path.GetFileName(Location);

    public bool IsPdf => SourceFormats.FromFileName(Name) == SourceFormats.Pdf;
}

/// <summary>
/// What a check reads, as the Windows app lists it: the PDFs, e-books and images in a folder and its subfolders, with
/// those in its ZIPs, or in one ZIP; ZIPs inside ZIPs, password-protected members and the like are listed with why
/// they aren't read.
/// </summary>
public static class DownloadCheckItems
{
    /// <summary>The files picked to check, in order, with each ZIP's members listed by <paramref name="members"/>.</summary>
    public static async Task<IReadOnlyList<CheckTarget>> ListAsync(IReadOnlyList<PickedFile> files, bool folder,
        Func<int, Task<IReadOnlyList<ZipMember>?>> members)
    {
        var items = new List<CheckTarget>();
        foreach (var file in files.OrderBy(f => f.Path, NaturalOrder.Instance))
        {
            // Folder names are read below the folder picked, or beside the ZIP picked, as on Windows.
            var relative = folder && file.Path.IndexOf('/') is var slash and >= 0 ? file.Path[(slash + 1)..] : file.Path;
            if (SourceFormats.IsArchive(file.Path))
            {
                if (await members(file.Id) is not { } inside) items.Add(new CheckTarget(file.Id, null, file.Path, relative, ArchiveProblems.Unreadable));
                else items.AddRange(Members(file, relative, inside));
            }
            else if (SourceFormats.FromFileName(file.Path) is not null) items.Add(new CheckTarget(file.Id, null, file.Path, relative));
        }
        return items;
    }

    static IEnumerable<CheckTarget> Members(PickedFile zip, string relative, IReadOnlyList<ZipMember> members)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in members)
        {
            var name = System.IO.Path.GetFileName(member.Name.Replace('\\', '/'));
            if (name.Length == 0) continue;
            var nested = SourceFormats.IsArchive(name);
            if (!nested && SourceFormats.FromFileName(name) is null) continue;
            // A ZIP can hold the same name twice; only the first is read, as Explorer shows only one.
            if (!seen.Add(member.Name)) continue;
            var problem = nested ? ArchiveProblems.Nested
                : member.Encrypted ? ArchiveProblems.Encrypted
                : member.Size > ArchiveProblems.MaxMemberBytes ? ArchiveProblems.TooBig
                : !member.Readable ? ArchiveProblems.Damaged
                : null;
            yield return new CheckTarget(zip.Id, member.Name, ArchivePaths.MemberPath(zip.Path, member.Name),
                ArchivePaths.MemberPath(relative, member.Name), problem);
        }
    }
}
