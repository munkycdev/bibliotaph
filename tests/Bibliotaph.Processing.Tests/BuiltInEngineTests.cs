using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bibliotaph.Classification;

namespace Bibliotaph.Processing.Tests;

public sealed class BuiltInEngineTests : IDisposable
{
    readonly string _folder = Directory.CreateTempSubdirectory("bibliotaph-engine-").FullName;
    readonly string _server;
    readonly string _model;

    public BuiltInEngineTests()
    {
        _server = Path.Combine(_folder, "llama-server.exe");
        _model = Path.Combine(_folder, "Model-Q4_K_M.gguf");
        File.WriteAllText(_server, "");
        File.WriteAllText(_model, "");
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>An engine that never was: it "runs" until disposed, or until the test says it stopped.</summary>
    sealed class StandIn(EngineStart start, string output = "") : IEngineProcess
    {
        public EngineStart Start { get; } = start;

        public bool Stopped { get; set; }

        public bool Disposed { get; private set; }

        public bool HasExited => Stopped || Disposed;

        public string RecentOutput => output;

        public void Dispose() => Disposed = true;
    }

    /// <summary>/health: 503 for the first <paramref name="loading"/> asks, as llama-server answers while it loads, then 200.</summary>
    sealed class Health(int loading = 1) : HttpMessageHandler
    {
        public int Asked { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("/health", request.RequestUri!.AbsolutePath);
            Assert.Equal("127.0.0.1", request.RequestUri.Host);
            Asked++;
            var status = Asked <= loading ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("{}", Encoding.UTF8, "application/json") });
        }
    }

    (BuiltInEngine Engine, List<StandIn> Starts) Engine(BuiltInEngineOptions? options = null, Func<EngineStart, StandIn>? start = null, int loading = 1)
    {
        var starts = new List<StandIn>();
#pragma warning disable CA2000 // The engine's client owns the handler.
        var engine = new BuiltInEngine((options ?? new BuiltInEngineOptions()) with { ServerPath = _server }, new HttpClient(new Health(loading)),
            s =>
            {
                var process = start?.Invoke(s) ?? new StandIn(s);
                starts.Add(process);
                return process;
            });
#pragma warning restore CA2000
        return (engine, starts);
    }

    static Task<string> Answer(BuiltInEngine engine, string model, int context) =>
        engine.UseAsync(model, context, (address, key) => Task.FromResult($"{address} {key}"), Ct);

    [Fact]
    public async Task Starts_on_first_use_on_this_computer_only_and_keeps_running_for_the_next()
    {
        var (engine, starts) = Engine();
        await using (engine)
        {
            Assert.False(engine.IsRunning);
            var first = await Answer(engine, _model, 12_288);
            var second = await Answer(engine, _model, 8_192);

            var start = Assert.Single(starts).Start;
            Assert.Equal(first, second);
            Assert.StartsWith($"http://127.0.0.1:{start.Port}/ ", first, StringComparison.Ordinal);
            Assert.EndsWith(start.ApiKey, first, StringComparison.Ordinal);
            Assert.Equal(32, start.ApiKey.Length);
            // The least context it starts with, even for a book that needs less.
            Assert.Equal(16_384, start.ContextTokens);
            Assert.Equal(["--model", _model, "--host", "127.0.0.1", "--port"], start.Arguments.Take(5));
            Assert.DoesNotContain(start.ApiKey, start.Arguments);
            Assert.True(engine.IsRunning);
        }
        Assert.True(starts[0].Disposed);
    }

    [Fact]
    public async Task Starts_again_with_more_room_when_a_book_needs_it_and_never_with_less()
    {
        var (engine, starts) = Engine();
        await using var _ = engine;
        await Answer(engine, _model, 12_288);
        await Answer(engine, _model, 24_576);
        await Answer(engine, _model, 12_288);

        Assert.Equal([16_384, 24_576], starts.Select(s => s.Start.ContextTokens));
        Assert.True(starts[0].Disposed);
        Assert.NotEqual(starts[0].Start.ApiKey, starts[1].Start.ApiKey);
    }

    [Fact]
    public async Task Stops_after_the_idle_time_and_when_asked()
    {
        var (engine, starts) = Engine(new BuiltInEngineOptions { IdleStop = TimeSpan.FromMilliseconds(200) });
        await using var _ = engine;
        await Answer(engine, _model, 8_192);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (engine.IsRunning && DateTime.UtcNow < deadline) await Task.Delay(50, Ct);
        Assert.False(engine.IsRunning);
        Assert.True(starts[0].Disposed);

        await Answer(engine, _model, 8_192);
        Assert.Equal(2, starts.Count);
        await engine.StopAsync();
        Assert.False(engine.IsRunning);
        Assert.True(starts[1].Disposed);
    }

    [Fact]
    public async Task Says_when_the_engine_or_the_model_is_missing()
    {
        var (engine, starts) = Engine();
        await using var _ = engine;
        var noModel = await Assert.ThrowsAsync<ModelEndpointException>(() => Answer(engine, Path.Combine(_folder, "missing.gguf"), 8_192));
        Assert.Equal(BuiltInEngine.NoModel, noModel.Message);

        File.Delete(_server);
        var noEngine = await Assert.ThrowsAsync<ModelEndpointException>(() => Answer(engine, _model, 8_192));
        Assert.Equal(BuiltInEngine.NotInstalled, noEngine.Message);
        Assert.False(engine.IsInstalled);
        Assert.Empty(starts);
    }

    [Fact]
    public async Task An_engine_that_stops_while_loading_says_what_it_said()
    {
        var (engine, starts) = Engine(start: s => new StandIn(s, "llama_model_load: error loading model") { Stopped = true });
        await using var _ = engine;

        var ex = await Assert.ThrowsAsync<ModelEndpointException>(() => Answer(engine, _model, 8_192));

        Assert.IsNotType<ModelRequestException>(ex);
        Assert.Equal("The built-in AI engine couldn't load the model. It said: llama_model_load: error loading model", ex.Message);
        Assert.False(engine.IsRunning);
    }

    [Fact]
    public async Task An_engine_that_stops_while_answering_fails_that_request_and_starts_again_for_the_next()
    {
        var (engine, starts) = Engine(start: s => new StandIn(s, "ggml_vulkan: out of memory"));
        await using var _ = engine;

        var ex = await Assert.ThrowsAsync<ModelRequestException>(() => engine.UseAsync<string>(_model, 8_192, (_, _) =>
        {
            starts[^1].Stopped = true;
            throw new ModelEndpointException("Nothing is answering.");
        }, Ct));

        Assert.Equal("The built-in AI engine stopped while answering. It said: ggml_vulkan: out of memory", ex.Message);
        await Answer(engine, _model, 8_192);
        Assert.Equal(2, starts.Count);
    }

    [Fact]
    public async Task The_client_caps_the_answer_and_switches_thinking_off()
    {
        JsonNode? sent = null;
        var chat = new Chat(body =>
        {
            sent = body;
            return """{"choices":[{"message":{"content":"{\"ok\":true}"}}]}""";
        });
        var (engine, starts) = Engine();
        await using var _ = engine;
#pragma warning disable CA2000 // The client owns the handler.
        var client = new BuiltInModelClient(engine, new HttpClient(chat), _model);
#pragma warning restore CA2000

        var answer = await client.CompleteAsync(new ModelRequest("ignored", "rules", "book", """{"type":"object"}""", 12_288), Ct);

        Assert.Equal("""{"ok":true}""", answer);
        Assert.Equal($"http://127.0.0.1:{starts[0].Start.Port}/v1/chat/completions", chat.Uri!.ToString());
        Assert.Equal(starts[0].Start.ApiKey, chat.Key);
        Assert.Equal(2_048, sent!["max_tokens"]!.GetValue<int>());
        Assert.Equal(0, sent["temperature"]!.GetValue<int>());
        Assert.False(sent["chat_template_kwargs"]!["enable_thinking"]!.GetValue<bool>());
        var info = await client.ConnectAsync(Ct);
        Assert.Equal(("built-in", "Model-Q4_K_M (built in)"), (info.Provider, Assert.Single(info.Models)));
    }

    [Fact]
    public void Models_are_found_by_id_and_file_and_suggested_by_graphics_memory()
    {
        const long GiB = 1L << 30;
        var folder = Path.Combine(_folder, "models");

        Assert.Equal("Qwen3 8B (built in)", BuiltInModels.NameFor("qwen3-8b"));
        Assert.Equal("Qwen3 8B (built in)", BuiltInModels.NameFor(Path.Combine(folder, "Qwen3-8B-Q4_K_M.gguf")));
        Assert.Equal("Model-Q4_K_M (built in)", BuiltInModels.NameFor(_model));
        Assert.Equal(Path.Combine(folder, "Qwen3-4B-Q4_K_M.gguf"), BuiltInModels.PathFor("qwen3-4b", folder));
        Assert.Equal(_model, BuiltInModels.PathFor(_model, folder));
        Assert.Equal("qwen3-8b", BuiltInModels.Suggest(8 * GiB).Id);
        Assert.Equal("qwen3-4b", BuiltInModels.Suggest(6 * GiB).Id);
        Assert.Equal("qwen3-4b", BuiltInModels.Suggest(null).Id);
        Assert.All(BuiltInModels.All, m => Assert.Equal(64, m.Sha256.Length));
        Assert.Equal("https://huggingface.co/Qwen/Qwen3-8B-GGUF/resolve/7c41481f57cb95916b40956ab2f0b139b296d974/Qwen3-8B-Q4_K_M.gguf",
            BuiltInModels.Qwen8B.DownloadUrl.ToString());
    }

    [Fact]
    public void A_built_in_setup_keeps_the_model_server_for_a_switch_back()
    {
        var server = new AiSetup("http://localhost:11434", "qwen3:8b", "ollama", true, false, new HashSet<long>());
        var builtIn = server with { BuiltIn = true, BuiltInModel = "qwen3-8b" };

        Assert.True(builtIn.IsReady);
        Assert.True(builtIn.IsLocal);
        Assert.Equal(("built-in", "Qwen3 8B (built in)"), (builtIn.RunProvider, builtIn.ModelName));
        Assert.Equal(("ollama", "qwen3:8b"), (server.RunProvider, server.ModelName));
        Assert.False((builtIn with { BuiltInModel = null }).IsReady);
        Assert.True((AiSetup.Off with { Enabled = true, BuiltIn = true, BuiltInModel = "qwen3-4b" }).IsReady);
        Assert.Equal(server, builtIn with { BuiltIn = false, BuiltInModel = null });
    }

    sealed class Chat(Func<JsonNode?, string> answer) : HttpMessageHandler
    {
        public Uri? Uri { get; private set; }

        public string? Key { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri = request.RequestUri;
            Key = request.Headers.Authorization?.Parameter;
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer(body), Encoding.UTF8, "application/json") };
        }
    }

    /// <summary>
    /// The real llama-server from build/fetch-engine.ps1, on the 260K-parameter story model, on the processor: it starts,
    /// answers in the schema it was given, and stops. In CI on Windows the engine must be there; elsewhere the test skips.
    /// </summary>
    [Fact]
    public async Task The_real_engine_starts_answers_in_the_schema_and_stops()
    {
        var root = RepoRoot();
        var server = Path.Combine(root, "artifacts", "aiworker", "llama-server.exe");
        var model = Path.Combine(root, "artifacts", "test-models", "stories260K.gguf");
        var required = OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true";
        if (!File.Exists(server) || !File.Exists(model))
        {
            Assert.False(required, "CI runs build/fetch-engine.ps1 first, so the engine and its test model should be in artifacts.");
            Assert.Skip("The engine isn't downloaded here: run build/fetch-engine.ps1 on Windows.");
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        await using var engine = new BuiltInEngine(new BuiltInEngineOptions { ServerPath = server, MinContextTokens = 2_048, MaxAnswerTokens = 64 }, http);
        var client = new BuiltInModelClient(engine, http, model);
        const string Schema = """{"type":"object","properties":{"ok":{"type":"boolean"}},"required":["ok"],"additionalProperties":false}""";

        var answer = await client.CompleteAsync(new ModelRequest("ignored", "Answer with JSON.", "Is this working?", Schema, 2_048), Ct);

        using var json = JsonDocument.Parse(answer);
        Assert.Contains(json.RootElement.GetProperty("ok").ValueKind, new[] { JsonValueKind.True, JsonValueKind.False });
        Assert.True(engine.IsRunning);
        Assert.StartsWith("llama.cpp ", engine.Version, StringComparison.Ordinal);
        await engine.StopAsync();
        Assert.False(engine.IsRunning);
    }

    static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Bibliotaph.slnx"))) return dir.FullName;
        throw new InvalidOperationException("The repository root isn't above the test's folder.");
    }
}
