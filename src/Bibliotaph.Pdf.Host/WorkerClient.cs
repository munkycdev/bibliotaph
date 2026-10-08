using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.IO.Pipes;
using Bibliotaph.Pdf.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bibliotaph.Pdf.Host;

public sealed record WorkerOptions
{
    /// <summary>Defaults to pdfworker\Bibliotaph.PdfWorker(.exe) under the running app's folder.</summary>
    public string? WorkerPath { get; init; }
    public long SharedBufferBytes { get; init; } = 256L * 1024 * 1024;

    /// <summary>Per-process committed-memory cap, enforced by a Windows job object.</summary>
    public long MemoryLimitBytes { get; init; } = 1024L * 1024 * 1024;
    public TimeSpan DefaultTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>A name for logs, such as the pool slot.</summary>
    public string Name { get; init; } = "worker";

    public static string DefaultWorkerPath =>
        Path.Combine(AppContext.BaseDirectory, "pdfworker", OperatingSystem.IsWindows() ? "Bibliotaph.PdfWorker.exe" : "Bibliotaph.PdfWorker");
}

/// <summary>
/// Owns one PdfWorker process. Requests are serialized; a crash or timeout kills the worker,
/// and the next request starts a fresh one. Callers must reopen documents after a restart
/// (compare <see cref="Generation"/>). Pages that kill the worker are reported to the
/// <see cref="PoisonTracker"/>, and poisoned pages are refused without starting a worker.
/// </summary>
public sealed class WorkerClient : IAsyncDisposable
{
    readonly PoisonTracker _poison;
    readonly ILogger _log;
    readonly SemaphoreSlim _gate = new(1, 1);
    readonly string _sharedPath;
    readonly FileStream _sharedFile;
    readonly ConcurrentQueue<string> _stderr = new();
    readonly MemoryMappedFile _mmf;
    readonly MemoryMappedViewAccessor _view;
    readonly unsafe byte* _shared;

    /// <summary>Paths of documents open in the current worker generation, for poison attribution.</summary>
    readonly Dictionary<int, string> _openDocs = [];

    Process? _process;
    NamedPipeServerStream? _pipe;
    JobObject? _job;
    int _nextId;

    public WorkerClient(WorkerOptions? options = null, PoisonTracker? poison = null, ILogger<WorkerClient>? log = null)
    {
        Options = options ?? new WorkerOptions();
        _poison = poison ?? new PoisonTracker();
        _log = log ?? NullLogger<WorkerClient>.Instance;
        _sharedPath = Path.Combine(Path.GetTempPath(), $"bibliotaph-pdf-{Guid.NewGuid():N}.bin");
        // The path overload opens the file with FileShare.Read, which on Windows locks the worker out.
        // Open it ourselves so the worker can map it read-write too.
        _sharedFile = new FileStream(_sharedPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete,
            1, FileOptions.DeleteOnClose);
        _mmf = MemoryMappedFile.CreateFromFile(_sharedFile, null, Options.SharedBufferBytes, MemoryMappedFileAccess.ReadWrite,
            HandleInheritability.None, leaveOpen: false);
        _view = _mmf.CreateViewAccessor(0, Options.SharedBufferBytes, MemoryMappedFileAccess.Read);
        unsafe
        {
            byte* pointer = null;
            _view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
            _shared = pointer;
        }
    }

    public WorkerOptions Options { get; }

    /// <summary>Incremented each time a running worker is killed or found dead.</summary>
    public int Restarts { get; private set; }

    /// <summary>Generation of the current worker process; documents opened in an earlier generation are gone.</summary>
    public int Generation { get; private set; }

    public bool IsRunning => _process is { HasExited: false };

    /// <summary>The current worker's process ID, or null when no worker is running.</summary>
    public int? WorkerProcessId => IsRunning ? _process!.Id : null;

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
        (await SendCoreAsync<object>(request, null, timeout, ct)).Response;

    /// <summary>Renders and copies the pixels out of shared memory before another request can overwrite them.</summary>
    public async Task<(Response Response, byte[]? Pixels)> RenderAsync(Request request, TimeSpan? timeout = null, CancellationToken ct = default) =>
        await RenderAsync(request, static (info, pixels) =>
        {
            var copy = GC.AllocateUninitializedArray<byte>(info.Stride * info.Height);
            unsafe { new ReadOnlySpan<byte>((void*)pixels, copy.Length).CopyTo(copy); }
            return copy;
        }, timeout, ct);

    /// <summary>
    /// Renders and hands <paramref name="consume"/> the pixels while they are still in shared memory (BGRA, top-down,
    /// <see cref="RenderInfo.Stride"/> bytes a row), so a caller that builds a bitmap copies them once. It runs before
    /// any other request can overwrite the buffer and must not keep the pointer.
    /// </summary>
    public async Task<(Response Response, T? Result)> RenderAsync<T>(Request request, Func<RenderInfo, nint, T> consume,
        TimeSpan? timeout = null, CancellationToken ct = default) =>
        await SendCoreAsync(request with { Op = Op.Render }, consume, timeout, ct);

    async Task<(Response Response, T? Result)> SendCoreAsync<T>(Request request, Func<RenderInfo, nint, T>? consume, TimeSpan? timeout, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var page = PageFor(request);
            if (page is not null && _poison.IsPoisoned(page)) throw new PagePoisonedException(page);

            if (!IsRunning) await StartAsync(ct);
            ct.ThrowIfCancellationRequested();
            var limit = timeout ?? Options.DefaultTimeout;
            // Once a request is written, its answer is read whatever the caller does: abandoning it would leave the
            // answer in the pipe for the next request. Only the timeout cuts it short, and that kills the worker.
            using var cts = new CancellationTokenSource(limit);
            try
            {
                await Framing.WriteAsync(_pipe!, request with { Id = ++_nextId }, cts.Token);
                var response = await Framing.ReadAsync<Response>(_pipe!, cts.Token)
                    ?? throw Died("pipe closed");

                Track(request, response);
                T? result = default;
                if (consume is not null && response is { Ok: true, Render: { } info })
                {
                    if ((long)info.Stride * info.Height > Options.SharedBufferBytes)
                        throw new WorkerException($"The worker reported a {info.Width}x{info.Height} bitmap larger than the shared buffer.");
                    unsafe { result = consume(info, (nint)_shared); }
                }
                return (response, result);
            }
            catch (OperationCanceledException)
            {
                _log.LogWarning("PDF {Worker} did not answer {Op} within {Timeout}; killing it", Options.Name, request.Op, limit);
                Kill();
                RecordKill(page);
                throw new WorkerTimeoutException(limit);
            }
            catch (IOException ex)
            {
                var died = Died(ex.Message);
                RecordKill(page);
                throw died;
            }
            catch (WorkerCrashedException)
            {
                RecordKill(page);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Which page a request touches, for poison tracking; null when it touches no file.</summary>
    PageKey? PageFor(Request request)
    {
        var path = request.Path is { Length: > 0 } p ? Path.GetFullPath(p) : _openDocs.GetValueOrDefault(request.DocId);
        if (path is null) return null;
        var page = request.Op is Op.Open || (request.Op is Op.Find && request.PageIndex < 0) ? PageKey.WholeFile : request.PageIndex;
        return new PageKey(path, page);
    }

    void Track(Request request, Response response)
    {
        if (request.Op is Op.Open && response is { Ok: true, Doc: { } doc } && request.Path is { Length: > 0 } path)
            _openDocs[doc.DocId] = Path.GetFullPath(path);
        else if (request.Op is Op.Close)
            _openDocs.Remove(request.DocId);
    }

    void RecordKill(PageKey? page)
    {
        if (page is null) return;
        var kills = _poison.RecordKill(page);
        if (kills >= PoisonTracker.KillsToPoison)
            _log.LogError("Page {Page} of {Path} killed the PDF worker {Kills} times; it is now poisoned", page.PageIndex, page.Path, kills);
    }

    WorkerCrashedException Died(string reason)
    {
        var detail = reason;
        try
        {
            if (_process is not null && _process.WaitForExit(2000))
            {
                _process.WaitForExit();   // the untimed overload also waits for the last stderr lines
                detail = string.Create(CultureInfo.InvariantCulture, $"exit code 0x{_process.ExitCode:X8}, {reason}");
            }
        }
        catch (InvalidOperationException) { }
        Kill();
        var stderr = string.Join(" | ", _stderr);
        _log.LogWarning("PDF {Worker} exited unexpectedly: {Detail}. Worker said: {Stderr}", Options.Name, detail, stderr);
        return new WorkerCrashedException(stderr.Length == 0 ? detail : $"{detail} Worker said: {stderr}");
    }

    async Task StartAsync(CancellationToken ct)
    {
        Kill();
        var pipeName = $"bibliotaph-pdf-{Guid.NewGuid():N}";
        _pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        var workerPath = Options.WorkerPath ?? WorkerOptions.DefaultWorkerPath;
        var psi = new ProcessStartInfo(workerPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        psi.ArgumentList.Add("--pipe");
        psi.ArgumentList.Add(pipeName);
        psi.ArgumentList.Add("--shared");
        psi.ArgumentList.Add(_sharedPath);
        psi.ArgumentList.Add("--shared-size");
        psi.ArgumentList.Add(Options.SharedBufferBytes.ToString(CultureInfo.InvariantCulture));

        _stderr.Clear();
        _openDocs.Clear();
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
            // One job per worker, so the peak-memory reading covers this worker only. The worker is
            // assigned before it connects, and it reads no PDF until it has connected and been asked to.
            _job = new JobObject(Options.MemoryLimitBytes);
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
        _log.LogInformation("PDF {Worker} started (pid {Pid}, generation {Generation}, cap {CapMb} MB)",
            Options.Name, _process.Id, Generation, Options.MemoryLimitBytes / (1024 * 1024));
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
        _openDocs.Clear();
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
            _gate.Dispose();
        }
    }
}
