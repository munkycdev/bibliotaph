using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Spike.Bench;

namespace Spike.Web;

/// <summary>The worker's entry points for Spike.Bench.Library. Every method returns JSON for spike.js to record.</summary>
[SupportedOSPlatform("browser")]
public static partial class IndexBench
{
    const string Folder = "bibliotaph";
    static Library? _library;

    [JSExport]
    public static string Sqlite()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sqlite_version(); CREATE VIRTUAL TABLE t USING fts5(x, tokenize = 'unicode61 remove_diacritics 2');";
        return Json(new { version = (string)command.ExecuteScalar()!, fts5 = true });
    }

    /// <summary>Opens index.db at <paramref name="path"/>: under /opfs/ through the OPFS VFS, anywhere else in memory.</summary>
    [JSExport]
    public static async Task<string> Open(string path)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        if (path.StartsWith("/opfs/", StringComparison.Ordinal))
        {
            var name = path[6..];
            await Opfs.OpenAsync(Folder, name, name + "-journal", name + "-wal", name + "-shm");
        }
        (_library, var result) = await Library.OpenAsync(path);
        return Json(new { result, ms = watch.ElapsedMilliseconds });
    }

    [JSExport]
    public static async Task<string> Close()
    {
        if (_library is not null) await _library.CloseAsync();
        _library = null;
        Opfs.Close();
        return Json(new { closed = true });
    }

    [JSExport]
    public static async Task<string> AddBooks(int first, int count, int pagesPerBook) =>
        Json(await Open().AddBooksAsync(first, count, pagesPerBook));

    [JSExport]
    public static async Task<string> Search(string query) => Json(await Open().SearchAsync(query));

    [JSExport]
    public static string Queries() => Json(Library.Queries);

    [JSExport]
    public static string Stats(string path) => Json(Library.Stats(path));

    [JSExport]
    public static async Task<string> RemoveOpfs()
    {
        await Opfs.RemoveAsync(Folder);
        return Json(new { removed = Folder });
    }

    [JSExport]
    public static async Task<string> OpfsBytes() => Json(new { bytes = await Opfs.BytesAsync(Folder) });

    static Library Open() => _library ?? throw new InvalidOperationException("Open first.");

    static string Json(object value) => JsonSerializer.Serialize(value);
}
