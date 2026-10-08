using Bibliotaph.Core;

namespace Bibliotaph.Processing.Tests;

public sealed class DataPurgeTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("bibliotaph-purge-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Starting_over_deletes_the_library_data_at_the_next_start_and_keeps_everything_else()
    {
        var paths = new AppPaths(Path.Combine(_dir, "data"));
        foreach (var directory in paths.Directories) Directory.CreateDirectory(directory);
        string[] data = ["catalog.db", "catalog.db-wal", "catalog.db-shm", "index.db", "index.db-journal"];
        string[] kept = ["catalog.db.mine", "notes.txt", Path.Combine("logs", "bibliotaph-20261008.log")];
        foreach (var name in data.Concat(kept)) File.WriteAllText(Path.Combine(paths.Root, name), "x");
        Directory.CreateDirectory(Path.Combine(paths.Cache, "covers"));
        File.WriteAllText(Path.Combine(paths.Cache, "covers", "a.jpg"), "x");
        File.WriteAllText(Path.Combine(paths.Backups, "catalog-20261008.db"), "x");
        var library = Directory.CreateDirectory(Path.Combine(_dir, "RPG Library")).FullName;
        File.WriteAllText(Path.Combine(library, "Book.pdf"), "x");

        Assert.False(DataPurge.RunIfRequested(paths));
        Assert.True(File.Exists(Path.Combine(paths.Root, "catalog.db")));

        DataPurge.Request(paths);
        Assert.True(DataPurge.RunIfRequested(paths));

        Assert.All(data, name => Assert.False(File.Exists(Path.Combine(paths.Root, name)), name));
        Assert.False(Directory.Exists(paths.Cache));
        Assert.False(Directory.Exists(paths.Backups));
        Assert.All(kept, name => Assert.True(File.Exists(Path.Combine(paths.Root, name)), name));
        Assert.True(File.Exists(Path.Combine(library, "Book.pdf")));
        Assert.False(DataPurge.RunIfRequested(paths));
    }
}
