using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace Bibliotaph.Processing.Tests;

public sealed class BuiltInModelDownloadTests : IDisposable
{
    readonly string _folder = Directory.CreateTempSubdirectory("bibliotaph-models-").FullName;
    readonly byte[] _file = RandomNumberGenerator.GetBytes(3_000_000);
    readonly BuiltInModel _model;

    public BuiltInModelDownloadTests() =>
        _model = new BuiltInModel("test", "Test model", "example/test-GGUF", "0123abcd", "test.gguf", _file.Length,
            Convert.ToHexStringLower(SHA256.HashData(_file)), 1L << 30, "Apache-2.0", new Uri("https://example.com/LICENSE"));

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    string Final => Path.Combine(_folder, "models", "test.gguf");

    string Part => Final + ".part";

    /// <summary>Hugging Face as far as a download sees it: the file, from a range when asked, cut off or held when told to.</summary>
    sealed class Server(byte[] file) : HttpMessageHandler
    {
        public List<RangeHeaderValue?> Ranges { get; } = [];

        public bool IgnoreRange { get; init; }

        /// <summary>Stops sending (the connection drops) after this many bytes.</summary>
        public long? CutOffAt { get; set; }

        /// <summary>Sends this many bytes, then waits until the download is stopped.</summary>
        public long? HoldAt { get; set; }

        public TaskCompletionSource Holding { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("https://huggingface.co/example/test-GGUF/resolve/0123abcd/test.gguf", request.RequestUri!.ToString());
            var range = request.Headers.Range;
            Ranges.Add(range);
            var from = !IgnoreRange && range?.Ranges.Single().From is { } start ? start : 0;
            var response = new HttpResponseMessage(from > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
            {
                Content = new StreamContent(new Body(this, file, from)),
            };
            return Task.FromResult(response);
        }

        sealed class Body(Server server, byte[] file, long position) : Stream
        {
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => position; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (position == server.HoldAt)
                {
                    server.Holding.TrySetResult();
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }
                if (position == server.CutOffAt) throw new IOException("The connection was reset.");
                if (position >= file.Length) return 0;
                var end = new[] { file.Length, server.CutOffAt ?? long.MaxValue, server.HoldAt ?? long.MaxValue }.Where(e => e > position).Min();
                var count = (int)Math.Min(Math.Min(buffer.Length, 256 * 1024), end - position);
                file.AsMemory((int)position, count).CopyTo(buffer);
                position += count;
                return count;
            }
        }
    }

    BuiltInModelDownloads Downloads(Server server, long free = long.MaxValue)
    {
#pragma warning disable CA2000 // The downloads' client owns the handler.
        return new BuiltInModelDownloads(new BuiltInEngineOptions { ModelsFolder = Path.Combine(_folder, "models") }, new HttpClient(server), _ => free);
#pragma warning restore CA2000
    }

    [Fact]
    public async Task Downloads_checks_and_names_the_model_only_once_it_matches()
    {
        var server = new Server(_file);
        var downloads = Downloads(server);
        BuiltInModel? done = null;
        downloads.Downloaded += (_, m) => done = m;
        Assert.Equal(BuiltInModelState.None, downloads.StateOf(_model));

        Assert.True(await downloads.DownloadAsync(_model, Ct));

        Assert.Equal(_file, await File.ReadAllBytesAsync(Final, Ct));
        Assert.False(File.Exists(Part));
        Assert.Equal(BuiltInModelState.Downloaded, downloads.StateOf(_model));
        Assert.Same(_model, done);
        Assert.Null(downloads.Current);
        Assert.Null(downloads.Problem);
        Assert.Null(Assert.Single(server.Ranges));
    }

    [Fact]
    public async Task A_dropped_connection_keeps_what_arrived_and_the_next_download_carries_on_from_there()
    {
        var server = new Server(_file) { CutOffAt = 1_000_000 };
        var downloads = Downloads(server);

        Assert.False(await downloads.DownloadAsync(_model, Ct));
        Assert.StartsWith("The download stopped: The connection was reset.", downloads.Problem, StringComparison.Ordinal);
        Assert.Equal(BuiltInModelState.Partial, downloads.StateOf(_model));
        Assert.Equal(1_000_000, downloads.PartialBytes(_model));

        server.CutOffAt = null;
        Assert.True(await downloads.DownloadAsync(_model, Ct));
        Assert.Equal(1_000_000, server.Ranges[1]!.Ranges.Single().From);
        Assert.Equal(_file, await File.ReadAllBytesAsync(Final, Ct));
    }

    [Fact]
    public async Task A_server_that_ignores_the_range_sends_it_all_again_and_that_is_kept_instead()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Part)!);
        await File.WriteAllBytesAsync(Part, _file.AsMemory(0, 500_000).ToArray(), Ct);
        var downloads = Downloads(new Server(_file) { IgnoreRange = true });

        Assert.True(await downloads.DownloadAsync(_model, Ct));

        Assert.Equal(_file, await File.ReadAllBytesAsync(Final, Ct));
    }

    [Fact]
    public async Task A_file_that_does_not_match_is_deleted()
    {
        var other = (byte[])_file.Clone();
        other[^1] ^= 0xff;
        var downloads = Downloads(new Server(other));

        Assert.False(await downloads.DownloadAsync(_model, Ct));

        Assert.Equal("The downloaded file isn't the Test model Bibliotaph expects, so it was deleted. Press Download to try again.", downloads.Problem);
        Assert.Equal(BuiltInModelState.None, downloads.StateOf(_model));
        Assert.False(File.Exists(Final));
    }

    [Fact]
    public async Task Without_room_on_the_drive_nothing_is_downloaded()
    {
        var server = new Server(_file);
        var downloads = Downloads(server, free: _file.Length);

        Assert.False(await downloads.DownloadAsync(_model, Ct));

        Assert.StartsWith("There isn't room for Test model: it needs 0.5 GB free and the drive has 0.0 GB.", downloads.Problem, StringComparison.Ordinal);
        Assert.Empty(server.Ranges);
    }

    [Fact]
    public async Task Pause_keeps_what_arrived_and_cancel_deletes_it()
    {
        var server = new Server(_file) { HoldAt = 1_048_576 };
        var downloads = Downloads(server);

        var paused = downloads.DownloadAsync(_model, Ct);
        await server.Holding.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        downloads.Pause();
        Assert.False(await paused);
        Assert.Null(downloads.Problem);
        Assert.Equal(1_048_576, downloads.PartialBytes(_model));

        var cancelled = downloads.DownloadAsync(_model, Ct);
        await Task.Delay(50, Ct);
        downloads.Cancel(_model);
        Assert.False(await cancelled);
        Assert.Equal(BuiltInModelState.None, downloads.StateOf(_model));
        Assert.Null(downloads.Problem);
    }

    [Fact]
    public async Task Delete_removes_the_model()
    {
        var downloads = Downloads(new Server(_file));
        await downloads.DownloadAsync(_model, Ct);

        downloads.Delete(_model);

        Assert.Equal(BuiltInModelState.None, downloads.StateOf(_model));
    }
}
