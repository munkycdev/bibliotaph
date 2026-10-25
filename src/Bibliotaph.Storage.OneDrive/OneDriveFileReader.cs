using Bibliotaph.Processing;

namespace Bibliotaph.Storage.OneDrive;

/// <summary>
/// Reads a book on a OneDrive by downloading it whole to a temporary file, which is deleted as soon as the stream is
/// closed: the server keeps no copy of anyone's books. Readers seek (PDFs, ZIPs), which a download stream can't.
/// Anything that isn't a drive path is read by <paramref name="local"/>.
/// </summary>
public sealed class OneDriveFileReader(IDriveClients drives, string temporaryFolder, ISourceFileReader local) : ISourceFileReader
{
    public Stream OpenRead(string path)
    {
        if (!DrivePath.TryParse(path, out var driveId, out var pathInDrive)) return local.OpenRead(path);
        if (drives.Find(driveId) is not { } client) throw new FileNotFoundException("That drive isn't connected.", path);
        // The interface is synchronous, as file opening is; callers run on the thread pool, never a UI thread.
        return DownloadAsync(client, driveId, pathInDrive).GetAwaiter().GetResult();
    }

    async Task<Stream> DownloadAsync(OneDriveClient client, string driveId, string pathInDrive)
    {
        Directory.CreateDirectory(temporaryFolder);
        var file = new FileStream(Path.Combine(temporaryFolder, $"{Guid.NewGuid():N}.book"), FileMode.CreateNew, FileAccess.ReadWrite,
            FileShare.None, bufferSize: 1 << 16, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        try
        {
            await using (var download = await client.OpenReadAsync(driveId, pathInDrive))
                await download.CopyToAsync(file);
            file.Position = 0;
            return file;
        }
        catch (OneDriveException ex) when (ex.Status == System.Net.HttpStatusCode.NotFound)
        {
            await file.DisposeAsync();
            throw new FileNotFoundException("The file isn't on the drive any more.", pathInDrive, ex);
        }
        catch (HttpRequestException ex)
        {
            await file.DisposeAsync();
            throw new IOException($"OneDrive couldn't be reached: {ex.Message}", ex);
        }
        catch
        {
            await file.DisposeAsync();
            throw;
        }
    }
}
