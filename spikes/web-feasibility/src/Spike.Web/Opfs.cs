using System.Runtime.InteropServices;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;

namespace Spike.Web;

/// <summary>The OPFS-backed SQLite VFS (Native/opfsvfs.c) and the handle pool it reads through (wwwroot/opfs.js).</summary>
[SupportedOSPlatform("browser")]
static partial class Opfs
{
    static bool _imported;

    /// <summary>Opens handles for <paramref name="names"/> in <paramref name="folder"/> and makes the VFS SQLite's default.</summary>
    public static async Task OpenAsync(string folder, params string[] names)
    {
        await ImportAsync();
        await OpenPool(folder, string.Join('|', names));
        // Opening any connection initialises SQLitePCLRaw, which must happen before the VFS registers.
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:")) connection.Open();
        var rc = Register();
        if (rc != 0) throw new InvalidOperationException($"The OPFS VFS failed to register: SQLite error {rc}.");
    }

    public static void Close()
    {
        if (!_imported) return;
        // The pool's ids are reused for whatever files open next, so the VFS must write out and forget this pool first.
        var rc = Reset();
        ClosePool();
        if (rc != 0) throw new IOException($"Writing the OPFS cache failed: SQLite error {rc}.");
    }

    public static async Task RemoveAsync(string folder)
    {
        await ImportAsync();
        await RemoveFolder(folder);
    }

    public static async Task<long> BytesAsync(string folder)
    {
        await ImportAsync();
        return (long)await FolderBytes(folder);
    }

    static async Task ImportAsync()
    {
        if (_imported) return;
        await JSHost.ImportAsync("opfs", "/opfs.js");
        _imported = true;
    }

    [JSImport("openPool", "opfs")]
    private static partial Task OpenPool(string folder, string names);

    [JSImport("closePool", "opfs")]
    private static partial void ClosePool();

    [JSImport("removeFolder", "opfs")]
    private static partial Task RemoveFolder(string folder);

    [JSImport("folderBytes", "opfs")]
    private static partial Task<double> FolderBytes(string folder);

    [DllImport("opfsvfs", EntryPoint = "bib_opfs_register")]
    private static extern int Register();

    [DllImport("opfsvfs", EntryPoint = "bib_opfs_reset")]
    private static extern int Reset();
}
