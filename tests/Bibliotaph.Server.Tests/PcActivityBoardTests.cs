using Bibliotaph.Api.Contracts;
using Bibliotaph.Server.Libraries;

namespace Bibliotaph.Server.Tests;

public sealed class PcActivityBoardTests
{
    readonly ManualClock _clock = new(new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero));

    static PcActivity Busy(long toClassify) => new("Classify", null, false, null, 0, toClassify, 0, 0);

    [Fact]
    public void A_PC_is_shown_to_its_own_user_until_it_stops_saying_or_says_it_has_nothing()
    {
        var board = new PcActivityBoard(_clock);
        board.Report("dana", "pc-1", "Workshop", Busy(10));
        board.Report("dana", "pc-2", "Attic", Busy(5));
        board.Report("sam", "pc-3", "Den", Busy(1));

        var both = board.Of("dana").Select(e => e.DeviceName).ToList();
        _clock.Advance(TimeSpan.FromSeconds(60));
        board.Report("dana", "pc-1", "Workshop", Busy(9));
        _clock.Advance(TimeSpan.FromSeconds(60));
        // The attic PC said nothing for two minutes: closed or asleep.
        var stillSaying = board.Of("dana").Select(e => (e.DeviceName, e.Activity.ToClassify)).ToList();
        board.Forget("dana", "pc-1");

        Assert.Equal(["Attic", "Workshop"], both);
        Assert.Equal([("Workshop", 9L)], stillSaying);
        Assert.Empty(board.Of("dana"));
    }
}
