using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;

namespace Bibliotaph.Processing.Tests;

public sealed class BuiltInEngineDownloadTests : IDisposable
{
    readonly string _folder = Directory.CreateTempSubdirectory("bibliotaph-engine-download-").FullName;
    readonly byte[] _release = Release();

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    string EngineFolder => Path.Combine(_folder, "engine");

    /// <summary>A release laid out as llama.cpp's are: everything in one folder, with tools the app doesn't use.</summary>
    static byte[] Release()
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var name in new[] { "llama-server.exe", "llama-server-impl.dll", "llama-cli.exe", "llama-cli-impl.dll", "ggml-vulkan.dll", "LICENSE-LLVM-OpenMP" })
            {
                using var writer = new StreamWriter(zip.CreateEntry($"llama-b1-bin-win-vulkan-x64/{name}").Open());
                writer.Write(name);
            }
            using var stray = new StreamWriter(zip.CreateEntry("docs/README.md").Open());
            stray.Write("not beside the server");
        }
        return memory.ToArray();
    }

    sealed class GitHub(byte[] body) : HttpMessageHandler
    {
        public List<Uri> Asked { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Asked.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
        }
    }

    (BuiltInEngineDownload Download, BuiltInEngine Engine, GitHub Server) Setup(string? sha256 = null, string release = "b1")
    {
        var engine = new BuiltInEngine(new BuiltInEngineOptions { ServerPath = Path.Combine(_folder, "aiworker", "llama-server.exe"), DownloadFolder = EngineFolder },
            new HttpClient());
        var server = new GitHub(_release);
        var pin = new EnginePin(release, "llama-b1-bin-win-vulkan-x64.zip", sha256 ?? Convert.ToHexStringLower(SHA256.HashData(_release)));
#pragma warning disable CA2000 // The download's client owns the handler.
        return (new BuiltInEngineDownload(engine, new HttpClient(server), pin), engine, server);
#pragma warning restore CA2000
    }

    [Fact]
    public async Task Downloads_the_pinned_release_and_keeps_only_the_server_and_what_it_loads()
    {
        var (download, engine, server) = Setup();
        await using var owned = engine;
        var downloaded = 0;
        download.Downloaded += (_, _) => downloaded++;
        Assert.True(download.IsNeeded);
        Assert.False(engine.IsInstalled);

        Assert.True(await download.EnsureAsync(Ct));

        Assert.Equal(new Uri("https://github.com/ggml-org/llama.cpp/releases/download/b1/llama-b1-bin-win-vulkan-x64.zip"), Assert.Single(server.Asked));
        Assert.Equal(["LICENSE-LLVM-OpenMP", "engine.txt", "ggml-vulkan.dll", "llama-server-impl.dll", "llama-server.exe"],
            Directory.GetFiles(EngineFolder).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal(["engine"], Directory.GetFileSystemEntries(_folder).Select(Path.GetFileName));
        Assert.True(engine.IsInstalled);
        Assert.Equal(Path.Combine(EngineFolder, "llama-server.exe"), engine.ServerPath);
        Assert.Equal("llama.cpp b1", engine.Version);
        Assert.Equal(1, downloaded);
        Assert.Null(download.Problem);

        // The next start finds it there.
        Assert.False(download.IsNeeded);
        Assert.True(await download.EnsureAsync(Ct));
        Assert.Single(server.Asked);
    }

    [Fact]
    public async Task A_file_that_does_not_match_the_pin_is_not_kept()
    {
        var (download, engine, _) = Setup(sha256: new string('0', 64));
        await using var owned = engine;

        Assert.False(await download.EnsureAsync(Ct));

        Assert.StartsWith("The built-in AI engine couldn't be downloaded: the file that arrived isn't the one Bibliotaph expects.", download.Problem, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFileSystemEntries(_folder));
        Assert.False(engine.IsInstalled);
        Assert.True(download.IsNeeded);
    }

    [Fact]
    public async Task Nothing_is_downloaded_when_the_engine_came_with_the_app()
    {
        var (download, engine, server) = Setup();
        await using var owned = engine;
        Directory.CreateDirectory(Path.Combine(_folder, "aiworker"));
        await File.WriteAllTextAsync(engine.Options.ServerPath, "", Ct);

        Assert.False(download.IsNeeded);
        Assert.True(await download.EnsureAsync(Ct));
        Assert.Empty(server.Asked);
        Assert.Equal(engine.Options.ServerPath, engine.ServerPath);
    }

    [Fact]
    public async Task A_new_pin_replaces_the_engine_downloaded_for_the_old_one()
    {
        Directory.CreateDirectory(EngineFolder);
        await File.WriteAllTextAsync(Path.Combine(EngineFolder, "llama-server.exe"), "old", Ct);
        await File.WriteAllTextAsync(Path.Combine(EngineFolder, "ggml-old.dll"), "old", Ct);
        await File.WriteAllTextAsync(Path.Combine(EngineFolder, "engine.txt"), "llama.cpp b0", Ct);
        var (download, engine, _) = Setup(release: "b1");
        await using var owned = engine;
        Assert.True(download.IsNeeded);

        Assert.True(await download.EnsureAsync(Ct));

        Assert.Equal("llama.cpp b1", engine.Version);
        Assert.False(File.Exists(Path.Combine(EngineFolder, "ggml-old.dll")));
    }

    [Fact]
    public void The_app_downloads_the_release_build_fetch_engine_pins()
    {
        var pin = EnginePin.Embedded;
        Assert.Matches("^b[0-9]+$", pin.Release);
        Assert.EndsWith("-bin-win-vulkan-x64.zip", pin.Asset, StringComparison.Ordinal);
        Assert.Matches("^[0-9a-f]{64}$", pin.Sha256);
    }
}
