namespace Bibliotaph.Server.Tests;

public sealed class UploadMemoryTests
{
    readonly ManualClock _clock = new(new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Uploads_share_the_budget_and_give_it_back()
    {
        var memory = new UploadMemory(_clock, 100);

        var first = memory.TryReserve(60);
        var refused = memory.TryReserve(41);
        first!.Dispose();
        var after = memory.TryReserve(100);

        Assert.NotNull(first);
        Assert.Null(refused);
        Assert.NotNull(after);
        Assert.Equal(100, memory.Taken);
    }

    [Fact]
    public void What_is_kept_beyond_the_request_lets_go_when_its_time_is_up()
    {
        var memory = new UploadMemory(_clock, 100);
        Assert.NotNull(memory.TryReserve(80, _clock.GetUtcNow() + TimeSpan.FromHours(2)));

        Assert.Null(memory.TryReserve(30));
        _clock.Advance(TimeSpan.FromHours(2));

        Assert.NotNull(memory.TryReserve(30));
        Assert.Equal(30, memory.Taken);
    }

    [Fact]
    public void A_library_pages_pictures_count_while_the_page_is_kept()
    {
        var memory = new UploadMemory(_clock, 100);
        var pages = new Libraries.HeldOnlinePages(_clock, memory);
        var reader = Bibliotaph.Processing.OnlineImports.Services[0];
        var first = pages.Find(pages.Keep(reader, new Bibliotaph.Processing.OnlinePage([], 0, true)))!;

        Assert.Null(first.AddImage("a.jpg", new byte[70]));
        Assert.Equal(UploadMemory.Busy, first.AddImage("b.jpg", new byte[40]));
        // The same picture again replaces itself rather than counting twice.
        Assert.Null(first.AddImage("a.jpg", new byte[90]));
        Assert.Equal(90, memory.Taken);
        for (var i = 0; i < 3; i++) pages.Keep(reader, new Bibliotaph.Processing.OnlinePage([], 0, true));
        Assert.Equal(0, memory.Taken);
    }
}
