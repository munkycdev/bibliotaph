namespace Bibliotaph.Core.Tests;

public sealed class PlayOrderTests
{
    [Fact]
    public void Plays_as_listed_from_the_track_picked_and_stops_at_the_end_back_on_the_first()
    {
        var order = new PlayOrder();
        order.Start(3, 1);
        Assert.Equal(1, order.Current);
        Assert.True(order.HasNext);
        Assert.Equal(2, order.Next(ended: true));
        Assert.False(order.HasNext);
        Assert.Null(order.Next(ended: true));
        Assert.Equal(0, order.Current);
    }

    [Fact]
    public void Repeat_all_goes_round_and_Previous_from_the_first_goes_to_the_last()
    {
        var order = new PlayOrder { Repeat = RepeatMode.All };
        order.Start(3, 2);
        Assert.True(order.HasNext);
        Assert.Equal(0, order.Next(ended: true));
        Assert.Equal(2, order.Previous());
        Assert.Equal(1, order.Previous());
    }

    [Fact]
    public void Previous_at_the_first_track_with_repeat_off_has_nowhere_to_go()
    {
        var order = new PlayOrder();
        order.Start(3, 0);
        Assert.Null(order.Previous());
        Assert.Equal(0, order.Current);
    }

    [Fact]
    public void Repeat_one_plays_the_track_again_when_it_ends_but_Next_still_moves_on()
    {
        var order = new PlayOrder { Repeat = RepeatMode.One };
        order.Start(3, 0);
        Assert.Equal(0, order.Next(ended: true));
        Assert.Equal(1, order.Next(ended: false));
    }

    [Fact]
    public void Shuffle_keeps_the_track_playing_first_and_plays_each_track_once()
    {
        var order = new PlayOrder(new Random(7));
        order.Start(10, 4);
        order.SetShuffle(true);
        Assert.Equal(4, order.Current);
        var played = new List<int> { order.Current!.Value };
        while (order.Next(ended: true) is { } next) played.Add(next);
        Assert.Equal(Enumerable.Range(0, 10), played.Order());
        Assert.Equal(4, played[0]);
    }

    [Fact]
    public void Shuffle_off_plays_on_as_listed_from_the_track_playing()
    {
        var order = new PlayOrder(new Random(3));
        order.Start(5, 0);
        order.SetShuffle(true);
        order.Next(ended: true);
        var playing = order.Current!.Value;
        order.SetShuffle(false);
        Assert.Equal(playing, order.Current);
        Assert.Equal(playing == 4 ? null : playing + 1, order.Next(ended: true));
    }

    [Fact]
    public void Picking_a_track_from_the_list_plays_it_and_carries_on_after_it()
    {
        var order = new PlayOrder();
        order.Start(4, 0);
        order.Jump(2);
        Assert.Equal(2, order.Current);
        Assert.Equal(3, order.Next(ended: true));
    }

    [Fact]
    public void An_empty_queue_has_no_current_track()
    {
        var order = new PlayOrder();
        order.Start(0, 0);
        Assert.Null(order.Current);
        Assert.False(order.HasNext);
        Assert.Null(order.Next(ended: true));
        Assert.Null(order.Previous());
    }

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(272, "4:32")]
    [InlineData(3735, "1:02:15")]
    public void Lengths_read_as_minutes_and_seconds_or_hours_past_an_hour(int seconds, string expected) =>
        Assert.Equal(expected, PlayOrder.Length(TimeSpan.FromSeconds(seconds)));
}
