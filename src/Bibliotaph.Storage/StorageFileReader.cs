using System.Net;
using Bibliotaph.Processing;

namespace Bibliotaph.Storage;

/// <summary>
/// Reads a book in connected storage by downloading it whole to a temporary file, which is deleted as soon as the
/// stream is closed: the server keeps no copy of anyone's books. Readers seek (PDFs, ZIPs), which a download stream
/// can't. Anything that isn't a storage path is read by <paramref name="local"/>. As <see cref="IRemoteFiles"/>, it
/// streams a file for <see cref="SourceFiles"/> to keep while its stages read it.
/// </summary>
public sealed class StorageFileReader(IStorageDrives drives, string temporaryFolder, ISourceFileReader local) : ISourceFileReader, IRemoteFiles
{
    public bool IsRemote(string path) => StoragePath.IsStoragePath(path);

    async Task<Stream> IRemoteFiles.OpenAsync(string path, CancellationToken ct)
    {
        var (drive, location) = Locate(path);
        try
        {
            return await drive.OpenReadAsync(location.Path, ct: ct);
        }
        catch (StorageException ex) when (ex.Status == HttpStatusCode.NotFound)
        {
            throw new FileNotFoundException("The file isn't in your storage any more.", location.Path, ex);
        }
        catch (HttpRequestException ex)
        {
            throw new IOException($"{drive.ProviderName} couldn't be reached: {ex.Message}", ex);
        }
    }

    public Stream OpenRead(string path)
    {
        if (!StoragePath.IsStoragePath(path)) return local.OpenRead(path);
        // The interface is synchronous, as file opening is; callers run on the thread pool, never a UI thread.
        return DownloadAsync(Locate(path)).GetAwaiter().GetResult();
    }

    (IStorageDrive Drive, StorageLocation Location) Locate(string path) =>
        drives.Locate(path) ?? throw new FileNotFoundException("That storage isn't connected.", path);

    async Task<Stream> DownloadAsync((IStorageDrive Drive, StorageLocation Location) at)
    {
        Directory.CreateDirectory(temporaryFolder);
        var file = new FileStream(Path.Combine(temporaryFolder, $"{Guid.NewGuid():N}.book"), FileMode.CreateNew, FileAccess.ReadWrite,
            FileShare.None, bufferSize: 1 << 16, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        try
        {
            await using (var download = await at.Drive.OpenReadAsync(at.Location.Path))
                await download.CopyToAsync(file);
            file.Position = 0;
            return file;
        }
        catch (StorageException ex) when (ex.Status == HttpStatusCode.NotFound)
        {
            await file.DisposeAsync();
            throw new FileNotFoundException("The file isn't in your storage any more.", at.Location.Path, ex);
        }
        catch (HttpRequestException ex)
        {
            await file.DisposeAsync();
            throw new IOException($"{at.Drive.ProviderName} couldn't be reached: {ex.Message}", ex);
        }
        catch
        {
            await file.DisposeAsync();
            throw;
        }
    }
}
