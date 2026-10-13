using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Bibliotaph.Classification;
using Bibliotaph.Core;
using Bibliotaph.Pdf.Host;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bibliotaph.Processing;

/// <summary>Where the built-in AI's engine is and how it is run.</summary>
public sealed record BuiltInEngineOptions
{
    /// <summary>llama.cpp's server, which the build puts in aiworker\ beside Bibliotaph.exe (build/fetch-engine.ps1).</summary>
    public string ServerPath { get; init; } = Path.Combine(AppContext.BaseDirectory, "aiworker", "llama-server.exe");

    /// <summary>
    /// Where the engine is downloaded at startup when this copy of Bibliotaph doesn't have one beside it (a build made
    /// without build/fetch-engine.ps1): %LOCALAPPDATA%\Bibliotaph\engine (<see cref="AppPaths.Engine"/>). None by default,
    /// so a test's engine is only ever the one it names.
    /// </summary>
    public string? DownloadFolder { get; init; }

    /// <summary>Where downloaded models are kept: %LOCALAPPDATA%\Bibliotaph\models (<see cref="AppPaths.Models"/>).</summary>
    public string ModelsFolder { get; init; } = AppPaths.ForCurrentUser().Models;

    /// <summary>How long the engine stays loaded with nothing to do, before it stops and gives the graphics memory back.</summary>
    public TimeSpan IdleStop { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Loading a 5 GB model from a hard disk takes a while; one that hasn't loaded by then isn't going to.</summary>
    public TimeSpan LoadTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>The least context the engine starts with. A book that needs more starts it again with more.</summary>
    public int MinContextTokens { get; init; } = 16_384;

    /// <summary>
    /// The most the engine is asked to write. An answer needs well under this, so a model stuck repeating itself stops
    /// here instead of running on, and its book fails by itself.
    /// </summary>
    public int MaxAnswerTokens { get; init; } = 2_048;

    /// <summary>
    /// The engine turns text into vectors instead of answering: llama-server's embedding mode, run as a second, small
    /// engine beside the chat one, so finding pages by meaning never unloads the chat model.
    /// </summary>
    public bool Embedding { get; init; }
}

/// <summary>How the engine is started: the server, the model file, its context, and where it listens.</summary>
public sealed record EngineStart(string ServerPath, string ModelPath, int ContextTokens, int Port, string ApiKey, bool Embedding = false)
{
    /// <summary>
    /// llama-server's arguments. It listens on this computer only. The key goes in its environment rather than here,
    /// so it isn't in the process list. llama.cpp fits the model's layers to the graphics card by itself. In embedding
    /// mode a text is read in one batch, so the batch is as big as the context: a page longer than the default batch
    /// would otherwise be refused.
    /// </summary>
    public IReadOnlyList<string> Arguments =>
    [
        "--model", ModelPath,
        "--host", "127.0.0.1",
        "--port", Port.ToString(CultureInfo.InvariantCulture),
        "--ctx-size", ContextTokens.ToString(CultureInfo.InvariantCulture),
        // One book at a time, so the whole context is that book's.
        "--parallel", "1",
        .. Embedding
            ? (string[])["--embedding", "--batch-size", ContextTokens.ToString(CultureInfo.InvariantCulture),
                "--ubatch-size", ContextTokens.ToString(CultureInfo.InvariantCulture)]
            : ["--jinja"],
        "--no-webui",
    ];

    /// <summary>The environment variable llama-server reads its API key from.</summary>
    public const string ApiKeyVariable = "LLAMA_API_KEY";
}

/// <summary>A running engine, as <see cref="BuiltInEngine"/> needs it.</summary>
public interface IEngineProcess : IDisposable
{
    bool HasExited { get; }

    /// <summary>The last few lines the engine wrote, for the message when it stops.</summary>
    string RecentOutput { get; }
}

/// <summary>llama-server as a process, in a job object so it never outlives the app.</summary>
public sealed class EngineProcess : IEngineProcess
{
    readonly Process _process;
    readonly ConcurrentQueue<string> _output = new();
    readonly JobObject? _job;

    EngineProcess(EngineStart start)
    {
        var info = new ProcessStartInfo(start.ServerPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            WorkingDirectory = Path.GetDirectoryName(start.ServerPath),
        };
        foreach (var argument in start.Arguments) info.ArgumentList.Add(argument);
        info.Environment[EngineStart.ApiKeyVariable] = start.ApiKey;
        _process = Process.Start(info) ?? throw new ModelEndpointException($"Couldn't start {start.ServerPath}.");
        _process.ErrorDataReceived += (_, e) => Keep(e.Data);
        _process.OutputDataReceived += (_, e) => Keep(e.Data);
        _process.BeginErrorReadLine();
        _process.BeginOutputReadLine();
        if (OperatingSystem.IsWindows())
        {
            _job = new JobObject(null);
            _job.Assign(_process);
        }
    }

    public static IEngineProcess Start(EngineStart start) => new EngineProcess(start);

    public bool HasExited => _process.HasExited;

    public string RecentOutput => string.Join(" | ", _output);

    void Keep(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        _output.Enqueue(line.Trim());
        while (_output.Count > 8) _output.TryDequeue(out _);
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            _process.WaitForExit(5000);
        }
        catch (InvalidOperationException)
        {
            // It had already gone.
        }
        _process.Dispose();
        if (OperatingSystem.IsWindows()) _job?.Dispose();
    }
}

/// <summary>
/// The built-in AI's engine: llama.cpp's server, started on the model when there is something to read, and stopped
/// after a few idle minutes, when AI is paused or switched off, and with the app. It listens on this computer only,
/// on a free port, with a key made for each start, so no other program can use it.
/// </summary>
public sealed class BuiltInEngine : IAsyncDisposable
{
    public const string NotInstalled = "The built-in AI engine isn't on this computer yet. Bibliotaph downloads it when it starts.";
    public const string NoModel = "The built-in AI's model isn't downloaded yet.";
    public const string ServerFile = "llama-server.exe";

    /// <summary>The service key of the second engine, in embedding mode, that Ask this book finds pages by meaning with.</summary>
    public const string EmbeddingKey = "embedding";

    readonly HttpClient _http;
    readonly Func<EngineStart, IEngineProcess> _start;
    readonly TimeProvider _clock;
    readonly ILogger _log;
    readonly SemaphoreSlim _gate = new(1, 1);
    IEngineProcess? _process;
    EngineStart? _running;
    ITimer? _idle;
    int _uses;

    /// <param name="options">Where the engine is, and how long it waits.</param>
    /// <param name="http">For the health check; requests go through the caller's own client.</param>
    /// <param name="start">Starts the engine; tests pass a stand-in.</param>
    /// <param name="clock">For the idle stop and the load timeout.</param>
    /// <param name="log">Starts, stops and failures; never anything of a book.</param>
    public BuiltInEngine(BuiltInEngineOptions options, HttpClient http, Func<EngineStart, IEngineProcess>? start = null, TimeProvider? clock = null,
        ILogger<BuiltInEngine>? log = null)
    {
        Options = options;
        _http = http;
        _start = start ?? EngineProcess.Start;
        _clock = clock ?? TimeProvider.System;
        _log = log ?? NullLogger<BuiltInEngine>.Instance;
    }

    public BuiltInEngineOptions Options { get; }

    /// <summary>The server beside Bibliotaph.exe if there is one, as an installed copy has; otherwise the downloaded one.</summary>
    public string ServerPath =>
        !File.Exists(Options.ServerPath) && DownloadedServerPath is { } downloaded && File.Exists(downloaded) ? downloaded : Options.ServerPath;

    /// <summary>Where the downloaded server goes, or null when this engine is never downloaded.</summary>
    public string? DownloadedServerPath => Options.DownloadFolder is { } folder ? Path.Combine(folder, ServerFile) : null;

    /// <summary>The server came with this copy of Bibliotaph, so nothing needs downloading.</summary>
    public bool IsBundled => File.Exists(Options.ServerPath);

    public bool IsInstalled => File.Exists(ServerPath);

    /// <summary>"llama.cpp b1234", from the note left beside the server, or null when there isn't one.</summary>
    public string? Version
    {
        get
        {
            var note = Path.Combine(Path.GetDirectoryName(ServerPath) ?? "", "engine.txt");
            return File.Exists(note) ? File.ReadAllText(note).Trim() : null;
        }
    }

    /// <summary>The engine is loaded and running.</summary>
    public bool IsRunning => _process is { HasExited: false };

    /// <summary>Raised when the engine starts or stops.</summary>
    public event EventHandler? StateChanged;

    /// <summary>
    /// Runs <paramref name="use"/> against the engine loaded with <paramref name="modelPath"/> and room for
    /// <paramref name="contextTokens"/>, starting it, or starting it again with more room, first. One use at a time:
    /// the engine answers one request at a time anyway.
    /// </summary>
    /// <exception cref="ModelEndpointException">The engine or the model is missing, or the engine couldn't load it.</exception>
    /// <exception cref="ModelRequestException">The engine stopped while answering.</exception>
    public async Task<T> UseAsync<T>(string modelPath, int contextTokens, Func<Uri, string, Task<T>> use, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _uses++;
            _idle?.Dispose();
            _idle = null;
            var (address, key) = await EnsureAsync(modelPath, contextTokens, ct);
            try
            {
                return await use(address, key);
            }
            catch (ModelEndpointException ex) when (_process is { HasExited: true } process)
            {
                // It stopped while answering: most likely this book was too much for it. The classifier's check that
                // follows starts it again, and if that answers, the book fails by itself.
                var said = process.RecentOutput;
                Stop("it stopped while answering");
                throw new ModelRequestException(Said("The built-in AI engine stopped while answering.", said), ex);
            }
        }
        finally
        {
            if (_process is not null)
            {
                var uses = _uses;
                _idle = _clock.CreateTimer(_ => _ = StopIfIdleAsync(uses), null, Options.IdleStop, Timeout.InfiniteTimeSpan);
            }
            _gate.Release();
        }
    }

    /// <summary>Stops the engine now, if it is running: AI was paused or switched off.</summary>
    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try
        {
            Stop("AI was paused or switched off");
        }
        finally
        {
            _gate.Release();
        }
    }

    async Task StopIfIdleAsync(int uses)
    {
        await _gate.WaitAsync();
        try
        {
            if (_uses == uses) Stop($"nothing to read for {Options.IdleStop.TotalMinutes:0} minutes");
        }
        finally
        {
            _gate.Release();
        }
    }

    async Task<(Uri Address, string Key)> EnsureAsync(string modelPath, int contextTokens, CancellationToken ct)
    {
        if (!IsInstalled) throw new ModelEndpointException(NotInstalled);
        if (!File.Exists(modelPath)) throw new ModelEndpointException(NoModel);
        if (_process is { HasExited: false } && _running is { } running && running.ModelPath == modelPath && running.ContextTokens >= contextTokens)
            return (Address(running), running.ApiKey);

        if (_process is { HasExited: true } dead) _log.LogWarning("The built-in AI engine had stopped by itself. It said: {Output}", dead.RecentOutput);
        // Never smaller than it was for this model, so books that needed more don't start it again each time.
        var context = Math.Max(Math.Max(contextTokens, Options.MinContextTokens), _running?.ModelPath == modelPath ? _running.ContextTokens : 0);
        Stop(_running?.ModelPath == modelPath ? "a book needs more room" : "another model was chosen");
        var start = new EngineStart(ServerPath, modelPath, context, FreePort(), Convert.ToHexString(RandomNumberGenerator.GetBytes(16)), Options.Embedding);
        var started = _clock.GetTimestamp();
        IEngineProcess process;
        try
        {
            process = _start(start);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            throw new ModelEndpointException($"The built-in AI engine couldn't start: {ex.Message}", ex);
        }
        _process = process;
        _running = start;
        await WaitUntilLoadedAsync(process, start, ct);
        _log.LogInformation("Built-in AI engine loaded {Model} with {Context} tokens of context in {Seconds:0.0} s",
            Path.GetFileName(modelPath), context, _clock.GetElapsedTime(started).TotalSeconds);
        StateChanged?.Invoke(this, EventArgs.Empty);
        return (Address(start), start.ApiKey);
    }

    /// <summary>llama-server answers /health with 503 while it loads the model and 200 once it can take requests.</summary>
    async Task WaitUntilLoadedAsync(IEngineProcess process, EngineStart start, CancellationToken ct)
    {
        var started = _clock.GetTimestamp();
        var health = new Uri(Address(start), "health");
        while (true)
        {
            if (process.HasExited)
            {
                var said = process.RecentOutput;
                Stop("it stopped while loading");
                throw new ModelEndpointException(Said("The built-in AI engine couldn't load the model.", said));
            }
            using (var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                attempt.CancelAfter(TimeSpan.FromSeconds(5));
                try
                {
                    using var response = await _http.GetAsync(health, attempt.Token);
                    if (response.IsSuccessStatusCode) return;
                }
                catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
                {
                    // Not listening yet.
                }
            }
            if (_clock.GetElapsedTime(started) > Options.LoadTimeout)
            {
                Stop("it didn't load in time");
                throw new ModelEndpointException($"The built-in AI engine didn't load the model within {Options.LoadTimeout.TotalMinutes:0} minutes.");
            }
            await Task.Delay(TimeSpan.FromMilliseconds(250), _clock, ct);
        }
    }

    void Stop(string why)
    {
        _idle?.Dispose();
        _idle = null;
        if (_process is null) return;
        var wasRunning = !_process.HasExited;
        _process.Dispose();
        _process = null;
        if (!wasRunning) return;
        _log.LogInformation("Built-in AI engine stopped: {Why}", why);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    static Uri Address(EngineStart start) => new($"http://127.0.0.1:{start.Port.ToString(CultureInfo.InvariantCulture)}/");

    static string Said(string message, string output) => output.Length == 0 ? message : $"{message} It said: {Last(output)}";

    /// <summary>The end of the engine's output, which is where it says what went wrong.</summary>
    static string Last(string output) => output.Length > 300 ? "…" + output[^300..] : output;

    static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            Stop("the app closed");
        }
        finally
        {
            _gate.Release();
        }
        _gate.Dispose();
    }
}

/// <summary>
/// The classifier's model client for the built-in AI: the engine's OpenAI-compatible API, with the answer's length
/// capped and Qwen3's thinking switched off. Temperature stays 0, as for any server, so a book gets the same answer
/// each time it is read.
/// </summary>
public sealed class BuiltInModelClient(BuiltInEngine engine, HttpClient http, string modelPath) : IModelClient
{
    /// <summary>
    /// llama.cpp's own request fields. No repetition penalty: the evidence check needs the book's words quoted exactly,
    /// and a penalty on repeats would push the model off them. llama.cpp's JSON grammar already bounds whitespace. A
    /// streamed request also gets how far the excerpt has been read, for the AI reading page.
    /// </summary>
    static readonly JsonObject Extras = new()
    {
        ["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = false },
        ["return_progress"] = true,
    };

    public string ModelPath => modelPath;

    public Task<EndpointInfo> ConnectAsync(CancellationToken ct = default)
    {
        if (!engine.IsInstalled) throw new ModelEndpointException(BuiltInEngine.NotInstalled);
        if (!File.Exists(modelPath)) throw new ModelEndpointException(BuiltInEngine.NoModel);
        return Task.FromResult(new EndpointInfo(EndpointKind.BuiltIn, engine.Version, [BuiltInModels.NameFor(modelPath)]));
    }

    public Task<string> CompleteAsync(ModelRequest request, CancellationToken ct = default)
    {
        var max = engine.Options.MaxAnswerTokens;
        var capped = request with { MaxTokens = Math.Min(request.MaxTokens ?? max, max) };
        return engine.UseAsync(modelPath, request.ContextTokens,
            (address, key) => new LocalModelClient(http, address.ToString(), () => key, EndpointKind.OpenAiCompatible, Extras).CompleteAsync(capped, ct), ct);
    }
}

/// <summary>
/// Ask this book's vectors from the built-in AI: the embedding engine's OpenAI-compatible embeddings call. The server
/// has one model loaded, so the model's name in the request doesn't matter.
/// </summary>
public sealed class BuiltInEmbeddingClient(BuiltInEngine engine, HttpClient http, string modelPath) : IEmbeddingClient
{
    public string ModelPath => modelPath;

    public Task<IReadOnlyList<float[]>> EmbedAsync(string model, IReadOnlyList<string> texts, CancellationToken ct = default) =>
        engine.UseAsync(modelPath, engine.Options.MinContextTokens,
            (address, key) => new LocalModelClient(http, address.ToString(), () => key, EndpointKind.OpenAiCompatible).EmbedAsync(model, texts, ct), ct);
}

/// <summary>
/// A model the built-in AI can download: pinned to one revision of one file on Hugging Face, with its size and
/// SHA-256, so what is downloaded is exactly what was tested.
/// </summary>
/// <param name="Id">What settings store: "qwen3-8b".</param>
/// <param name="Name">What people see, and with " (built in)" what runs are recorded under: "Qwen3 8B".</param>
/// <param name="Repo">The Hugging Face repository.</param>
/// <param name="Revision">The repository's commit, so a later upload can't change what is downloaded.</param>
/// <param name="File">The file in the repository, which is also its name in the models folder.</param>
/// <param name="Bytes">The file's size, for the free disk check and the progress bar.</param>
/// <param name="Sha256">The file's SHA-256, checked before the model is used.</param>
/// <param name="GraphicsBytes">The graphics memory it runs well in, with a book's worth of context.</param>
/// <param name="Licence">The model's licence, by name.</param>
/// <param name="LicenceUrl">Where the licence can be read, shown before the download starts.</param>
public sealed record BuiltInModel(string Id, string Name, string Repo, string Revision, string File, long Bytes, string Sha256,
    long GraphicsBytes, string Licence, Uri LicenceUrl)
{
    public Uri DownloadUrl => new($"https://huggingface.co/{Repo}/resolve/{Revision}/{File}");

    /// <summary>
    /// Its revision, size and SHA-256 are pinned, so it can be downloaded. A model listed before its file was checked
    /// has none, and isn't offered: its download could never pass the check.
    /// </summary>
    public bool IsPinned => Revision.Length > 0 && Bytes > 0 && Sha256.Length == 64;
}

/// <summary>The built-in AI's models, where they are kept, and the name runs are recorded under.</summary>
public static class BuiltInModels
{
    const long GiB = 1L << 30;

    /// <summary>Qwen3 8B, the default: the size the pilot was run with on Ollama, for an 8 GB graphics card.</summary>
    public static BuiltInModel Qwen8B { get; } = new("qwen3-8b", "Qwen3 8B", "Qwen/Qwen3-8B-GGUF", "7c41481f57cb95916b40956ab2f0b139b296d974",
        "Qwen3-8B-Q4_K_M.gguf", 5_027_783_488, "d98cdcbd03e17ce47681435b5150e34c1417f50b5c0019dd560e4882c5745785", 7 * GiB,
        "Apache-2.0", new("https://huggingface.co/Qwen/Qwen3-8B/blob/main/LICENSE"));

    /// <summary>Qwen3 4B, for smaller graphics cards and computers without one.</summary>
    public static BuiltInModel Qwen4B { get; } = new("qwen3-4b", "Qwen3 4B", "Qwen/Qwen3-4B-GGUF", "bc640142c66e1fdd12af0bd68f40445458f3869b",
        "Qwen3-4B-Q4_K_M.gguf", 2_497_280_256, "7485fe6f11af29433bc51cab58009521f205840f5b4ae3a32fa7f92e8534fdf5", 4 * GiB,
        "Apache-2.0", new("https://huggingface.co/Qwen/Qwen3-4B/blob/main/LICENSE"));

    /// <summary>The models offered, the default first.</summary>
    public static IReadOnlyList<BuiltInModel> All { get; } = [Qwen8B, Qwen4B];

    /// <summary>
    /// nomic-embed-text v1.5 (Apache-2.0), which finds a book's pages by meaning for Ask this book, run by a second
    /// engine in embedding mode. Pinned like the chat models: the repository's commit, the file's size and its SHA-256
    /// from the file's LFS pointer on Hugging Face.
    /// </summary>
    public static BuiltInModel Embedding { get; } = new("nomic-embed-text-v1.5", "nomic-embed-text v1.5", "nomic-ai/nomic-embed-text-v1.5-GGUF",
        "0188c9bf409793f810680a5a431e7b899c46104c", "nomic-embed-text-v1.5.Q8_0.gguf", 146_146_432,
        "3e24342164b3d94991ba9692fdc0dd08e3fd7362e0aacc396a9a5c54a544c3b7", 1 * GiB, "Apache-2.0", new("https://huggingface.co/nomic-ai/nomic-embed-text-v1.5"));

    public static BuiltInModel? Find(string? id) => All.FirstOrDefault(m => m.Id == id);

    /// <summary>The embedding model, when <paramref name="id"/> is its id.</summary>
    public static BuiltInModel? FindEmbedding(string? id) => Embedding.Id == id ? Embedding : null;

    /// <summary>
    /// The model to suggest for a computer with <paramref name="graphicsBytes"/> of graphics memory: the largest that
    /// fits, or the smallest when the memory isn't known or nothing fits.
    /// </summary>
    public static BuiltInModel Suggest(long? graphicsBytes) =>
        All.FirstOrDefault(m => graphicsBytes >= m.GraphicsBytes) ?? All[^1];

    /// <summary>
    /// "Qwen3 8B (built in)": a different model, in the records, from the same one on a model server. A model given by
    /// its id or its downloaded file goes by its name in the list; any other file (in tests) by the file's name.
    /// </summary>
    public static string NameFor(string model) =>
        $"{(Find(model) ?? FindEmbedding(model) ?? All.FirstOrDefault(m => Path.GetFileName(model) == m.File))?.Name ?? Path.GetFileNameWithoutExtension(model)} (built in)";

    /// <summary>Where <paramref name="model"/>'s file is, or would be once downloaded: in the models folder, or a path as given.</summary>
    public static string PathFor(string model, string modelsFolder) =>
        (Find(model) ?? FindEmbedding(model)) is { } known ? Path.Combine(modelsFolder, known.File) : model;
}

public static class BuiltInEngineWatch
{
    /// <summary>
    /// Stops the engine as soon as the Classify lane is paused, or AI is switched off or moved to a model server, so the
    /// graphics memory goes back at once rather than after the idle minutes.
    /// </summary>
    public static void StopWhenNotNeeded(this BuiltInEngine engine, AiSettings ai, IndexingService indexing)
    {
        void Check(object? sender, EventArgs e)
        {
            if (engine.IsRunning && (indexing.IsPaused(Lane.Classify) || !ai.Setup.IsReady || !ai.Setup.BuiltIn)) _ = engine.StopAsync();
        }
        ai.Changed += Check;
        indexing.Changed += Check;
    }

    /// <summary>
    /// Stops the embedding engine as soon as AI is switched off, moved to a model server or left without a built-in
    /// embedding model. Pausing the Classify lane leaves it, since Ask this book is asked for, not background work.
    /// </summary>
    public static void StopWhenNotNeededForAsk(this BuiltInEngine engine, AiSettings ai) =>
        ai.Changed += (_, _) =>
        {
            if (engine.IsRunning && (!ai.Setup.IsReady || !ai.Setup.BuiltIn || ai.Setup.BuiltInEmbeddingModel is null)) _ = engine.StopAsync();
        };
}
