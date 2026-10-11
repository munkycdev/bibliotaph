using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bibliotaph.Processing;

/// <summary>Why a model's download stopped: no room, a server that wouldn't send it, or a file that didn't match.</summary>
public sealed class ModelDownloadException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Where a model is: not downloaded, part way (paused, or cut off), or downloaded and checked.</summary>
public enum BuiltInModelState
{
    None,
    Partial,
    Downloaded,
}

/// <summary>A download as it goes: bytes so far of the model's size, and whether the finished file is being checked.</summary>
public sealed record ModelDownload(BuiltInModel Model, long Bytes, bool Checking)
{
    public double Fraction => Model.Bytes == 0 ? 0 : Math.Clamp((double)Bytes / Model.Bytes, 0, 1);
}

/// <summary>
/// The built-in AI's models on this computer (Built-in AI plan, choice 6): downloads one when Download is pressed, into
/// the models folder as "&lt;file&gt;.part", resuming that file after Pause or a restart, and renames it only once its
/// SHA-256 matches the pin. One download at a time, which outlives the Settings page that started it.
/// </summary>
public sealed class BuiltInModelDownloads(
    BuiltInEngineOptions options, HttpClient http, Func<string, long>? freeSpace = null, TimeProvider? clock = null,
    ILogger<BuiltInModelDownloads>? log = null)
{
    /// <summary>Room left on the drive after a download, so the library's own databases can still grow.</summary>
    public const long SpareBytes = 512L << 20;

    static readonly TimeSpan ReportEvery = TimeSpan.FromMilliseconds(250);

    readonly Func<string, long> _freeSpace = freeSpace ?? (folder => new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))!).AvailableFreeSpace);
    readonly TimeProvider _clock = clock ?? TimeProvider.System;
    readonly ILogger _log = (ILogger?)log ?? NullLogger.Instance;
    readonly Lock _gate = new();
    CancellationTokenSource? _stop;
    bool _discard;

    /// <summary>The download running now, or null.</summary>
    public ModelDownload? Current { get; private set; }

    /// <summary>Why the last download stopped, when it didn't finish and wasn't paused or cancelled.</summary>
    public string? Problem { get; private set; }

    /// <summary>Raised, on any thread, as a download starts, moves on, and ends.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised, on any thread, when a model has downloaded and passed its check.</summary>
    public event EventHandler<BuiltInModel>? Downloaded;

    public string PathFor(BuiltInModel model) => Path.Combine(options.ModelsFolder, model.File);

    string PartFor(BuiltInModel model) => PathFor(model) + ".part";

    /// <summary>
    /// A model counts as downloaded when its file is there at its pinned size: only a file whose SHA-256 matched is ever
    /// given that name, so it isn't hashed again on every start.
    /// </summary>
    public BuiltInModelState StateOf(BuiltInModel model) =>
        File.Exists(PathFor(model)) && new FileInfo(PathFor(model)).Length == model.Bytes ? BuiltInModelState.Downloaded
        : File.Exists(PartFor(model)) ? BuiltInModelState.Partial
        : BuiltInModelState.None;

    /// <summary>The bytes of a part-way download kept for resuming.</summary>
    public long PartialBytes(BuiltInModel model) => File.Exists(PartFor(model)) ? new FileInfo(PartFor(model)).Length : 0;

    /// <summary>
    /// Downloads <paramref name="model"/>, or carries on from where it stopped. Returns false when it was paused or
    /// cancelled; a problem is in <see cref="Problem"/>. Throws when another download is running.
    /// </summary>
    public async Task<bool> DownloadAsync(BuiltInModel model, CancellationToken ct = default)
    {
        CancellationTokenSource stop;
        lock (_gate)
        {
            if (Current is not null) throw new InvalidOperationException("A model is downloading already.");
            stop = _stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _discard = false;
            Current = new ModelDownload(model, PartialBytes(model), false);
            Problem = null;
        }
        Changed?.Invoke(this, EventArgs.Empty);
        try
        {
            await FetchAsync(model, stop.Token);
            _log.LogInformation("Downloaded the built-in AI's model {Model}", model.Id);
            Finish();
            Downloaded?.Invoke(this, model);
            return true;
        }
        catch (OperationCanceledException)
        {
            if (_discard) DeletePart(model);
            _log.LogInformation("{Action} the built-in AI's model {Model}", _discard ? "Cancelled downloading" : "Paused downloading", model.Id);
            Finish();
            return false;
        }
        catch (Exception ex) when (ex is ModelDownloadException or HttpRequestException or IOException)
        {
            _log.LogWarning(ex, "Could not download the built-in AI's model {Model}", model.Id);
            Finish(ex is ModelDownloadException ? ex.Message : $"The download stopped: {ex.Message} Press Download to carry on from where it got to.");
            return false;
        }
        finally
        {
            stop.Dispose();
        }
    }

    void Finish(string? problem = null)
    {
        lock (_gate)
        {
            Current = null;
            _stop = null;
            Problem = problem;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Stops the download and keeps what has arrived, for Download to carry on from.</summary>
    public void Pause()
    {
        lock (_gate) _stop?.Cancel();
    }

    /// <summary>Stops the download and deletes what has arrived. A paused download's part is deleted as well.</summary>
    public void Cancel(BuiltInModel model)
    {
        lock (_gate)
        {
            if (_stop is not null && Current?.Model == model)
            {
                _discard = true;
                _stop.Cancel();
                return;
            }
        }
        DeletePart(model);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Deletes a downloaded model, and any part of one. The engine must have stopped using it first.</summary>
    public void Delete(BuiltInModel model)
    {
        if (File.Exists(PathFor(model))) File.Delete(PathFor(model));
        DeletePart(model);
        _log.LogInformation("Deleted the built-in AI's model {Model}", model.Id);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    void DeletePart(BuiltInModel model)
    {
        if (File.Exists(PartFor(model))) File.Delete(PartFor(model));
    }

    async Task FetchAsync(BuiltInModel model, CancellationToken ct)
    {
        // Nothing could pass the check without a pin, so nothing is fetched.
        if (!model.IsPinned) throw new ModelDownloadException($"This version of Bibliotaph can't download {model.Name} yet.");
        Directory.CreateDirectory(options.ModelsFolder);
        var part = PartFor(model);
        var have = PartialBytes(model);
        if (have > model.Bytes)
        {
            File.Delete(part);
            have = 0;
        }
        var free = _freeSpace(options.ModelsFolder);
        if (free < model.Bytes - have + SpareBytes)
            throw new ModelDownloadException(
                $"There isn't room for {model.Name}: it needs {Gb(model.Bytes - have + SpareBytes)} free and the drive has {Gb(free)}. Make room, then press Download again.");

        if (have < model.Bytes)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, model.DownloadUrl);
            if (have > 0) request.Headers.Range = new RangeHeaderValue(have, null);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                throw new ModelDownloadException(Restart(part, "The server wouldn't carry on from where the download stopped."));
            if (!response.IsSuccessStatusCode)
                throw new ModelDownloadException($"Hugging Face wouldn't send {model.Name} ({(int)response.StatusCode} {response.ReasonPhrase}). Try again later.");
            // A server that ignores the range sends the whole file again: start over rather than append it.
            var append = have > 0 && response.StatusCode == HttpStatusCode.PartialContent;
            if (!append) have = 0;

            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using (var target = new FileStream(part, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20, useAsync: true))
            {
                var buffer = new byte[1 << 20];
                var reported = _clock.GetTimestamp();
                int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    if (have + read > model.Bytes)
                        throw new ModelDownloadException(Restart(part, $"The server sent more than {model.Name}'s size."));
                    await target.WriteAsync(buffer.AsMemory(0, read), ct);
                    have += read;
                    if (_clock.GetElapsedTime(reported) >= ReportEvery)
                    {
                        reported = _clock.GetTimestamp();
                        Report(model, have, false);
                    }
                }
            }
            if (have < model.Bytes)
                throw new ModelDownloadException($"The download stopped part way. Press Download to carry on from {Gb(have)}.");
        }

        Report(model, have, true);
        string actual;
        await using (var file = new FileStream(part, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true))
            actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct));
        if (actual != model.Sha256)
        {
            File.Delete(part);
            throw new ModelDownloadException($"The downloaded file isn't the {model.Name} Bibliotaph expects, so it was deleted. Press Download to try again.");
        }
        File.Move(part, PathFor(model), overwrite: true);
    }

    static string Restart(string part, string why)
    {
        File.Delete(part);
        return $"{why} What had arrived was deleted; press Download to start again.";
    }

    void Report(BuiltInModel model, long bytes, bool checking)
    {
        Current = new ModelDownload(model, bytes, checking);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>"4.7 GB": sizes as Windows Explorer shows them.</summary>
    public static string Gb(long bytes) => (bytes / (double)(1L << 30)).ToString(bytes >= 10L << 30 ? "0" : "0.0", CultureInfo.CurrentCulture) + " GB";
}

public static class BuiltInModelDownloadWatch
{
    /// <summary>
    /// Wakes the Classify lane when the model the built-in AI is set to finishes downloading, whether or not Settings is
    /// still open. Until then the lane waits, saying the model isn't downloaded yet.
    /// </summary>
    public static void WakeAiWhenDownloaded(this BuiltInModelDownloads downloads, AiSettings ai) =>
        downloads.Downloaded += (_, model) =>
        {
            if (ai.Setup.BuiltIn && (ai.Setup.BuiltInModel == model.Id || ai.Setup.BuiltInEmbeddingModel == model.Id)) ai.Refresh();
        };
}
