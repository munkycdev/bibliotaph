using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.IO.Pipes;
using Bibliotaph.Spike.Contracts;

namespace Bibliotaph.Spike.WorkerHost;

public sealed record WorkerOptions
{
    /// <summary>Defaults to PdfWorker(.exe) next to the running app.</summary>
    public string? WorkerPath { get; init; }
    public long SharedBufferBytes { get; init; } = 256L * 1024 * 1024;

    /// <summary>Per-process committed-memory cap, enforced by a Windows job object.</summary>
    public long MemoryLimitBytes { get; init; } = 1024L * 1024 * 1024;
    public TimeSpan DefaultTimeout { get; init; } = TimeSpan.FromSeconds(60);
}

public class WorkerException(string message) : Exception(message);
public sealed class WorkerTimeoutException(TimeSpan timeout) : WorkerException($"Worker did not answer within {timeout.TotalSeconds:0.#} s and was killed.");
public sealed class WorkerCrashedException(string detail) : WorkerException($"Worker exited unexpectedly ({detail}).");

/// <summary>
/// Owns one PdfWorker process. Requests are serialized; a crash or timeout kills the worker,
/// and the next request starts a fresh one. Callers must reopen documents after a restart.
/// </summary>
public sealed class WorkerClient : IAsyncDisposable
{
    readonly WorkerOptions _options;
    readonly SemaphoreSlim _gate = new(1, 1);
    readonly string _sharedPath;
    readonly FileStream _sharedFile;
    readonly System.Collections.Concurrent.ConcurrentQueue<string> _stderr = new();
    readonly MemoryMappedFile _mmf;
    readonly MemoryMappedViewAccessor _view;
    readonly unsafe byte* _shared;

    Process? _process;
    NamedPipeServerStream? _pipe;
    JobObject? _job;
    int _nextId;

    public WorkerClient(WorkerOptions? options = null)
    {
        _options = options ?? new WorkerOptions();
        _sharedPath = Path.Combine(Path.GetTempPath(), $"bibliotaph-spike-{Guid.NewGuid():N}.bin");
        // The path overload opens the file with FileShare.Read, which on Windows locks the worker out.
        // Open it ourselves so the worker can map it read-write too.
        _sharedFile = new FileStream(_sharedPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete,
            1, FileOptions.DeleteOnClose);
        _mmf = MemoryMappedFile.CreateFromFile(_sharedFile, null, _options.SharedBufferBytes, MemoryMappedFileAccess.ReadWrite,
            HandleInheritability.None, leaveOpen: false);
        _view = _mmf.CreateViewAccessor(0, _options.SharedBufferBytes, MemoryMappedFileAccess.Read);
        unsafe
        {
            byte* pointer = null;
            _view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
            _shared = pointer;
        }
    }

    /// <summary>Incremented each time a running worker is killed or found dead.</summary>
    public int Restarts { get; private set; }

    /// <summary>Generation of the current worker process; documents opened in an earlier generation are gone.</summary>
    public int Generation { get; private set; }

    public bool IsRunning => _process is { HasExited: false };

    /// <summary>Peak committed memory of the current worker (job object on Windows, peak working set elsewhere).</summary>
    public long? PeakMemoryBytes
    {
        get
        {
            if (OperatingSystem.IsWindows() && _job is not null) return _job.PeakProcessMemoryUsed;
            if (_process is null) return null;
            try { _process.Refresh(); return _process.PeakWorkingSet64; } catch (InvalidOperationException) { return null; }
        }
    }

    public async Task<Response> SendAsync(Request request, TimeSpan? timeout = null, CancellationToken ct = default) =>
        (await SendCoreAsync(request, copyPixels: false, timeout, ct)).Response;

    /// <summary>Renders and copies the pixels out of shared memory before another request can overwrite them.</summary>
    public async Task<(Response Response, byte[]? Pixels)> RenderAsync(Request request, TimeSpan? timeout = null, CancellationToken ct = default) =>
        await SendCoreAsync(request with { Op = Op.Render }, copyPixels: true, timeout, ct);

    async Task<(Response Response, byte[]? Pixels)> SendCoreAsync(Request request, bool copyPixels, TimeSpan? timeout, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!IsRunning) await StartAsync(ct);
            var limit = timeout ?? _options.DefaultTimeout;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(limit);
            try
            {
                await Framing.WriteAsync(_pipe!, request with { Id = ++_nextId }, cts.Token);
                var response = await Framing.ReadAsync<Response>(_pipe!, cts.Token)
                    ?? throw Died("pipe closed");

                byte[]? pixels = null;
                if (copyPixels && response is { Ok: true, Render: { } info })
                {
                    pixels = GC.AllocateUninitializedArray<byte>(info.Stride * info.Height);
                    unsafe { new ReadOnlySpan<byte>(_shared, pixels.Length).CopyTo(pixels); }
                }
                return (response, pixels);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Kill();
                throw new WorkerTimeoutException(limit);
            }
            catch (IOException ex)
            {
                throw Died(ex.Message);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    WorkerCrashedException Died(string reason)
    {
        var detail = reason;
        try
        {
            if (_process is not null && _process.WaitForExit(2000))
            {
                _process.WaitForExit();   // the untimed overload also waits for the last stderr lines
                detail = $"exit code 0x{_process.ExitCode:X8}, {reason}";
            }
        }
        catch (InvalidOperationException) { }
        Kill();
        var stderr = string.Join(" | ", _stderr);
        return new WorkerCrashedException(stderr.Length == 0 ? detail : $"{detail} Worker said: {stderr}");
    }

    async Task StartAsync(CancellationToken ct)
    {
        Kill();
        var pipeName = $"bibliotaph-spike-{Guid.NewGuid():N}";
        _pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        var workerPath = _options.WorkerPath
            ?? Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "PdfWorker.exe" : "PdfWorker");
        var psi = new ProcessStartInfo(workerPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        psi.ArgumentList.Add("--pipe");
        psi.ArgumentList.Add(pipeName);
        psi.ArgumentList.Add("--shared");
        psi.ArgumentList.Add(_sharedPath);
        psi.ArgumentList.Add("--shared-size");
        psi.ArgumentList.Add(_options.SharedBufferBytes.ToString());

        _stderr.Clear();
        _process = Process.Start(psi) ?? throw new WorkerException($"Could not start {workerPath}.");
        // Keep the last few lines the worker writes to stderr, so a crash report says why.
        _process.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            _stderr.Enqueue(e.Data.Trim());
            while (_stderr.Count > 6) _stderr.TryDequeue(out string? _);
        };
        _process.BeginErrorReadLine();
        if (OperatingSystem.IsWindows())
        {
            // One job per worker, so the peak-memory reading covers this worker only.
            _job = new JobObject(_options.MemoryLimitBytes);
            _job.Assign(_process);
        }

        using var connect = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connect.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            await _pipe.WaitForConnectionAsync(connect.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Kill();
            throw new WorkerException("Worker started but never connected.");
        }
        Generation++;
    }

    void Kill()
    {
        if (_process is not null)
        {
            try
            {
                if (!_process.HasExited) _process.Kill(entireProcessTree: true);
                _process.WaitForExit(2000);
            }
            catch (InvalidOperationException) { }
            _process.Dispose();
            _process = null;
            Restarts++;
        }
        _pipe?.Dispose();
        _pipe = null;
        if (OperatingSystem.IsWindows()) _job?.Dispose();
        _job = null;
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_process is not null) Restarts--; // a clean shutdown is not a restart
            Kill();
            _view.SafeMemoryMappedViewHandle.ReleasePointer();
            _view.Dispose();
            _mmf.Dispose();   // also closes _sharedFile, which deletes it
        }
        finally
        {
            _gate.Release();
        }
    }
}
