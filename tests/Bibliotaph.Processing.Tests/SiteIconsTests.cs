using System.Net;
using Bibliotaph.Core;

namespace Bibliotaph.Processing.Tests;

public sealed class SiteIconsTests : IDisposable
{
    static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    readonly string _root = Directory.CreateTempSubdirectory("bibliotaph-site-icons-").FullName;
    readonly Clock _clock = new(new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero));

    sealed class Clock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A stand-in for the web: each site answers with what <see cref="Answer"/> says, and every request is counted.</summary>
    sealed class Web : HttpMessageHandler
    {
        public List<Uri> Asked { get; } = [];

        public Func<Uri, HttpResponseMessage> Answer { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Asked) Asked.Add(request.RequestUri!);
            return Task.FromResult(Answer(request.RequestUri!));
        }
    }

    (SiteIcons Icons, Web Web) Setup()
    {
        var web = new Web();
#pragma warning disable CA2000 // The icons' client owns the handler.
        return (new SiteIcons(new AppPaths(_root), new HttpClient(web), _clock), web);
#pragma warning restore CA2000
    }

    [Theory]
    [InlineData("https://www.dndbeyond.com/sources/dnd/vault-job", "dndbeyond.com")]
    [InlineData("https://app.Demiplane.com/nexus", "app.demiplane.com")]
    [InlineData("http://example.com", "example.com")]
    [InlineData("file:///C:/Books/vault.pdf", null)]
    [InlineData("https://localhost/vault", null)]
    [InlineData("not a link", null)]
    public void A_site_is_its_host_without_www(string url, string? host) => Assert.Equal(host, SiteIcons.HostOf(url));

    [Fact]
    public async Task A_sites_icon_is_fetched_once_and_kept()
    {
        var (icons, web) = Setup();
        web.Answer = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Png) };

        var first = await icons.GetAsync("https://www.dndbeyond.com/sources/dnd/vault-job", Ct);
        var again = await icons.GetAsync("https://dndbeyond.com/en/library", Ct);

        Assert.Equal(Path.Combine(_root, "cache", "icons", "dndbeyond.com.png"), first);
        Assert.Equal(first, again);
        Assert.Equal([new Uri("https://dndbeyond.com/favicon.ico")], web.Asked);
    }

    [Fact]
    public async Task A_page_instead_of_an_icon_is_not_kept_and_the_site_is_left_alone_for_a_week()
    {
        var (icons, web) = Setup();
        web.Answer = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>Sign in</html>") };

        Assert.Null(await icons.GetAsync("https://www.example.com/a", Ct));
        Assert.Null(await icons.GetAsync("https://www.example.com/b", Ct));
        Assert.Single(web.Asked);
        Assert.Null(icons.Find("example.com"));

        _clock.Now += SiteIcons.RetryAfter + TimeSpan.FromMinutes(1);
        web.Answer = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Png) };
        Assert.NotNull(await icons.GetAsync("https://www.example.com/c", Ct));
        Assert.Equal(2, web.Asked.Count);
    }

    [Fact]
    public async Task A_site_that_cant_be_reached_has_no_icon()
    {
        var (icons, web) = Setup();
        web.Answer = _ => throw new HttpRequestException("No route to host");

        Assert.Null(await icons.GetAsync("https://www.example.com/a", Ct));
    }

    [Fact]
    public void An_icon_the_caller_has_replaces_one_kept_before()
    {
        var (icons, _) = Setup();
        byte[] ico = [0x00, 0x00, 0x01, 0x00, 0x01, 0x00];

        Assert.EndsWith("example.com.png", icons.Keep("example.com", Png), StringComparison.Ordinal);
        Assert.EndsWith("example.com.ico", icons.Keep("example.com", ico), StringComparison.Ordinal);
        Assert.Equal(Path.Combine(icons.Folder, "example.com.ico"), icons.Find("example.com"));
        Assert.False(File.Exists(Path.Combine(icons.Folder, "example.com.png")));
        Assert.Null(icons.Keep("example.com", "<html>"u8));
    }
}
