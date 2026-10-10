using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Bibliotaph.Classification.Tests;

public class LocalModelClientTests
{
    /// <summary>A model server in memory: answers by path, and remembers each request's path, body and key.</summary>
    sealed class FakeServer(Func<string, JsonNode?, (HttpStatusCode Status, string Body)> answer) : HttpMessageHandler
    {
        public List<(string Path, JsonNode? Body, string? Key)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken));
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add((path, body, request.Headers.Authorization?.Parameter));
            var (status, text) = answer(path, body);
            return new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
        }
    }

    static (LocalModelClient Client, FakeServer Server) Connect(string endpoint, Func<string, JsonNode?, (HttpStatusCode, string)> answer,
        string? key = null)
    {
        var server = new FakeServer(answer);
#pragma warning disable CA2000 // The client owns the handler and the test owns neither for longer than it runs.
        var client = new LocalModelClient(new HttpClient(server), endpoint, () => key);
#pragma warning restore CA2000
        return (client, server);
    }

    static ModelRequest Request => new("model-a", "the rules", "the book", ClassifierPrompt.SchemaJson, 12_288);

    static (HttpStatusCode, string) Ok(string body) => (HttpStatusCode.OK, body);

    static (HttpStatusCode, string) NotFound => (HttpStatusCode.NotFound, "404 page not found");

    [Theory]
    [InlineData("localhost:11434", "http://localhost:11434")]
    [InlineData("http://127.0.0.1:1234/v1/", "http://127.0.0.1:1234")]
    [InlineData("https://models.example.com/api/v1", "https://models.example.com/api")]
    [InlineData("ftp://localhost", null)]
    [InlineData("http://", null)]
    public void Tidies_an_endpoint_as_typed(string typed, string? expected) => Assert.Equal(expected, LocalModelClient.Tidy(typed));

    [Theory]
    [InlineData("http://localhost:11434", true)]
    [InlineData("http://127.0.0.1:1234", true)]
    [InlineData("http://192.168.1.20:11434", true)]
    [InlineData("http://10.0.0.5", true)]
    [InlineData("http://gpu-box:11434", true)]
    [InlineData("http://studio.local:1234", true)]
    [InlineData("http://[::1]:11434", true)]
    [InlineData("https://api.example.com", false)]
    [InlineData("http://8.8.8.8", false)]
    [InlineData("http://localhost.example.com", false)]
    public void Knows_which_endpoints_are_on_this_computer_or_network(string endpoint, bool local) =>
        Assert.Equal(local, LocalModelClient.IsLocal(endpoint));

    [Fact]
    public async Task Finds_ollama_and_its_models_and_asks_its_native_chat_with_the_context_window()
    {
        var (client, server) = Connect("http://localhost:11434", (path, _) => path switch
        {
            "/api/version" => Ok("""{"version":"0.12.3"}"""),
            "/api/tags" => Ok("""{"models":[{"name":"qwen3:8b"},{"name":"llama3.1:8b"}]}"""),
            "/api/chat" => Ok("""{"message":{"role":"assistant","content":"{\"title\":[]}"},"done":true}"""),
            _ => NotFound,
        });

        var info = await client.ConnectAsync(TestContext.Current.CancellationToken);
        var answer = await client.CompleteAsync(Request, TestContext.Current.CancellationToken);

        Assert.Equal(EndpointKind.Ollama, info.Kind);
        Assert.Equal("0.12.3", info.Version);
        Assert.Equal(["llama3.1:8b", "qwen3:8b"], info.Models);
        Assert.Equal("""{"title":[]}""", answer);
        var chat = server.Requests.Single(r => r.Path == "/api/chat").Body!;
        Assert.Equal(12_288, chat["options"]!["num_ctx"]!.GetValue<int>());
        Assert.Equal(0, chat["options"]!["temperature"]!.GetValue<int>());
        Assert.False(chat["stream"]!.GetValue<bool>());
        Assert.Equal("object", chat["format"]!["type"]!.GetValue<string>());
        Assert.Equal("system", chat["messages"]![0]!["role"]!.GetValue<string>());
    }

    [Fact]
    public async Task Uses_openai_chat_completions_with_a_json_schema_and_the_saved_key()
    {
        var (client, server) = Connect("http://127.0.0.1:1234/v1", (path, _) => path switch
        {
            "/v1/models" => Ok("""{"data":[{"id":"local-model"}]}"""),
            "/v1/chat/completions" => Ok("""{"choices":[{"message":{"content":"{}"}}]}"""),
            _ => NotFound,
        }, key: "secret-key");

        var info = await client.ConnectAsync(TestContext.Current.CancellationToken);
        var answer = await client.CompleteAsync(Request, TestContext.Current.CancellationToken);

        Assert.Equal(EndpointKind.OpenAiCompatible, info.Kind);
        Assert.Equal(["local-model"], info.Models);
        Assert.Equal("{}", answer);
        var completion = server.Requests.Single(r => r.Path == "/v1/chat/completions");
        Assert.Equal("secret-key", completion.Key);
        Assert.Equal("json_schema", completion.Body!["response_format"]!["type"]!.GetValue<string>());
        Assert.True(completion.Body["response_format"]!["json_schema"]!["strict"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Falls_back_to_json_mode_when_a_server_refuses_the_schema()
    {
        var (client, server) = Connect("http://127.0.0.1:1234", (path, body) => path switch
        {
            "/v1/models" => Ok("""{"data":[{"id":"m"}]}"""),
            "/v1/chat/completions" when body!["response_format"]!["type"]!.GetValue<string>() == "json_schema"
                => (HttpStatusCode.BadRequest, """{"error":{"message":"response_format json_schema is not supported"}}"""),
            "/v1/chat/completions" => Ok("""{"choices":[{"message":{"content":"{\"year\":[]}"}}]}"""),
            _ => NotFound,
        });

        var answer = await client.CompleteAsync(Request, TestContext.Current.CancellationToken);

        Assert.Equal("""{"year":[]}""", answer);
        Assert.Equal(2, server.Requests.Count(r => r.Path == "/v1/chat/completions"));
    }

    [Fact]
    public async Task Says_what_went_wrong_when_the_key_is_refused_or_nothing_answers()
    {
        var (refusing, _) = Connect("https://models.example.com", (path, _) => path == "/api/version"
            ? NotFound
            : (HttpStatusCode.Unauthorized, """{"error":"invalid api key"}"""));
        var refused = await Assert.ThrowsAsync<ModelEndpointException>(() => refusing.ConnectAsync(TestContext.Current.CancellationToken));
        Assert.Contains("needs a key", refused.Message, StringComparison.Ordinal);
        Assert.Contains("invalid api key", refused.Message, StringComparison.Ordinal);

        var (silent, _) = Connect("http://localhost:9", (_, _) => throw new HttpRequestException("Connection refused"));
        var unreachable = await Assert.ThrowsAsync<ModelEndpointException>(() => silent.ConnectAsync(TestContext.Current.CancellationToken));
        Assert.Contains("Nothing is answering", unreachable.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Something_that_is_not_a_model_server_is_not_mistaken_for_one()
    {
        var (client, _) = Connect("http://localhost:8080", (path, _) => path switch
        {
            "/api/version" => Ok("<html>hello</html>"),
            "/v1/models" => Ok("""{"status":"ok"}"""),
            _ => NotFound,
        });

        var ex = await Assert.ThrowsAsync<ModelEndpointException>(() => client.ConnectAsync(TestContext.Current.CancellationToken));
        Assert.Contains("not like a model server", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_classifier_sends_the_book_only_in_the_user_message_and_checks_the_answer()
    {
        var vocabulary = new Core.Metadata.Vocabulary([new Core.Metadata.Term("type", "adventure", "Adventure")]);
        var excerpt = new Excerpt([new ExcerptPage(0, "The Sunken Lantern. An adventure for 3rd level characters.", ExcerptPart.Opening)], [], 1);
        var (client, server) = Connect("http://localhost:11434", (path, _) => path switch
        {
            "/api/version" => Ok("""{"version":"0.12.3"}"""),
            "/api/tags" => Ok("""{"models":[]}"""),
            "/api/chat" => Ok("""
                {"message":{"content":"{\"title\":[{\"value\":\"The Sunken Lantern\",\"page\":1,\"quote\":\"The Sunken Lantern\"}],\"publisher\":[{\"value\":\"Made Up Press\",\"page\":1,\"quote\":\"The Sunken Lantern\"}]}"}}
                """),
            _ => NotFound,
        });
        var classifier = new ModelClassifier(client, "ollama", "model-a");

        var result = await classifier.ClassifyAsync(
            new ClassifierInput(excerpt, "lantern.pdf", p => p == 0 ? excerpt.Pages[0].Text : null, vocabulary), TestContext.Current.CancellationToken);

        Assert.Equal("The Sunken Lantern", Assert.Single(result.Accepted).Value);
        Assert.Equal("publisher", Assert.Single(result.Dropped).Claim.Field);
        var messages = server.Requests.Single(r => r.Path == "/api/chat").Body!["messages"]!.AsArray();
        Assert.DoesNotContain("Sunken", messages[0]!["content"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("<book-excerpt>", messages[1]!["content"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    const string RepeatLimit = """{"error":"prediction aborted, token repeat limit reached"}""";

    static ClassifierInput Lantern()
    {
        var excerpt = new Excerpt([new ExcerptPage(0, "The Sunken Lantern. An adventure for 3rd level characters.", ExcerptPart.Opening)], [], 1);
        return new ClassifierInput(excerpt, "lantern.pdf", p => p == 0 ? excerpt.Pages[0].Text : null, new Core.Metadata.Vocabulary([]));
    }

    static bool IsBook(JsonNode? body) =>
        body?["messages"]?[1]?["content"]?.GetValue<string>().Contains("<book-excerpt>", StringComparison.Ordinal) == true;

    [Fact]
    public async Task A_server_that_fails_while_answering_is_told_apart_from_one_that_refuses()
    {
        var (ollama, _) = Connect("http://localhost:11434", (path, _) => path switch
        {
            "/api/version" => Ok("""{"version":"0.12.3"}"""),
            "/api/tags" => Ok("""{"models":[]}"""),
            _ => (HttpStatusCode.InternalServerError, RepeatLimit),
        });
        var failed = await Assert.ThrowsAsync<ModelRequestException>(() => ollama.CompleteAsync(Request, TestContext.Current.CancellationToken));
        Assert.Contains("failed while answering (500)", failed.Message, StringComparison.Ordinal);
        Assert.Contains("token repeat limit reached", failed.Message, StringComparison.Ordinal);

        var (studio, _) = Connect("http://localhost:1234/v1", (path, _) => path switch
        {
            "/api/version" => NotFound,
            "/v1/models" => Ok("""{"data":[{"id":"model-a"}]}"""),
            _ => (HttpStatusCode.ServiceUnavailable, """{"error":{"message":"model crashed"}}"""),
        });
        failed = await Assert.ThrowsAsync<ModelRequestException>(() => studio.CompleteAsync(Request, TestContext.Current.CancellationToken));
        Assert.Contains("(503)", failed.Message, StringComparison.Ordinal);

        // A refusal is still about the endpoint, not the request.
        var (refusing, _) = Connect("http://localhost:11434", (path, _) => path == "/api/version"
            ? Ok("""{"version":"0.12.3"}""")
            : path == "/api/tags" ? Ok("""{"models":[]}""") : NotFound);
        var refused = await Assert.ThrowsAsync<ModelEndpointException>(() => refusing.CompleteAsync(Request, TestContext.Current.CancellationToken));
        Assert.IsNotType<ModelRequestException>(refused);
    }

    [Fact]
    public async Task A_model_that_takes_longer_than_allowed_is_a_failed_request_not_a_server_that_is_gone()
    {
        var server = new SlowChat();
#pragma warning disable CA2000 // The client owns the handler.
        using var http = new HttpClient(server) { Timeout = TimeSpan.FromMilliseconds(300) };
#pragma warning restore CA2000
        var client = new LocalModelClient(http, "http://localhost:11434");

        var ex = await Assert.ThrowsAsync<ModelRequestException>(() => client.CompleteAsync(Request, TestContext.Current.CancellationToken));

        Assert.Contains("in the time allowed", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>Ollama that answers everything at once except a chat, which it never finishes.</summary>
    sealed class SlowChat : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var text = request.RequestUri!.AbsolutePath switch
            {
                "/api/version" => """{"version":"0.12.3"}""",
                "/api/tags" => """{"models":[]}""",
                _ => null,
            };
            if (text is null) await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(text!, Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public async Task A_book_the_model_loops_on_is_that_books_problem_while_the_server_still_answers_a_check()
    {
        var (client, server) = Connect("http://localhost:11434", (path, body) => path switch
        {
            "/api/version" => Ok("""{"version":"0.12.3"}"""),
            "/api/tags" => Ok("""{"models":[]}"""),
            "/api/chat" when IsBook(body) => (HttpStatusCode.InternalServerError, RepeatLimit),
            "/api/chat" => Ok("""{"message":{"content":"{\"ok\":true}"}}"""),
            _ => NotFound,
        });
        var classifier = new ModelClassifier(client, "ollama", "model-a");

        var ex = await Assert.ThrowsAsync<ClassifierAnswerException>(() => classifier.ClassifyAsync(Lantern(), TestContext.Current.CancellationToken));

        Assert.Contains("couldn't read this book", ex.Message, StringComparison.Ordinal);
        Assert.Contains("token repeat limit reached", ex.Message, StringComparison.Ordinal);
        var chats = server.Requests.Where(r => r.Path == "/api/chat").Select(r => r.Body!).ToList();
        Assert.Equal(2, chats.Count);
        // The check carries nothing of the book, is capped short, and keeps the book's window so the model isn't reloaded.
        Assert.DoesNotContain("Lantern", chats[1].ToJsonString(), StringComparison.Ordinal);
        Assert.Equal(32, chats[1]["options"]!["num_predict"]!.GetValue<int>());
        Assert.Equal(chats[0]["options"]!["num_ctx"]!.GetValue<int>(), chats[1]["options"]!["num_ctx"]!.GetValue<int>());
        Assert.Equal("model-a", chats[1]["model"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_server_that_fails_every_request_is_the_servers_problem()
    {
        var (client, _) = Connect("http://localhost:11434", (path, _) => path switch
        {
            "/api/version" => Ok("""{"version":"0.12.3"}"""),
            "/api/tags" => Ok("""{"models":[]}"""),
            _ => (HttpStatusCode.InternalServerError, """{"error":"llama runner process has terminated"}"""),
        });
        var classifier = new ModelClassifier(client, "ollama", "model-a");

        var ex = await Assert.ThrowsAsync<ModelRequestException>(() => classifier.ClassifyAsync(Lantern(), TestContext.Current.CancellationToken));

        Assert.Contains("runner process has terminated", ex.Message, StringComparison.Ordinal);
    }
}
