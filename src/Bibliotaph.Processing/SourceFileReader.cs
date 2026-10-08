namespace Bibliotaph.Processing;

/// <summary>
/// The one way Bibliotaph opens a source file (a PDF, map or handout in the user's library).
/// Read-only by construction: there is no write path to call.
/// </summary>
public interface ISourceFileReader
{
    Stream OpenRead(string path);
}

public sealed class SourceFileReader : ISourceFileReader
{
    /// <summary>
    /// Shares read, write and delete so Bibliotaph never blocks the user, a sync client or another app
    /// from changing the file while it is being read; the reconciler notices such changes afterwards.
    /// </summary>
    public Stream OpenRead(string path) =>
        new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 1 << 16, FileOptions.SequentialScan | FileOptions.Asynchronous);
}
