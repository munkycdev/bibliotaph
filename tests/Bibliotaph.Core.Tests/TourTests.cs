namespace Bibliotaph.Core.Tests;

/// <summary>The tour's steps, worded once for every client.</summary>
public sealed class TourTests
{
    [Fact]
    public void The_mac_tour_points_at_the_same_controls_as_windows_in_its_own_words()
    {
        foreach (var chapter in Enum.GetValues<TourChapter>())
        {
            var windows = Tour.Steps(chapter, TourClient.Windows);
            var mac = Tour.Steps(chapter, TourClient.Mac);
            Assert.Equal(windows.Select(s => s.Anchors), mac.Select(s => s.Anchors));
            Assert.Equal(windows.Select(s => s.Action), mac.Select(s => s.Action));
        }

        var books = Tour.Steps(TourChapter.Books, TourClient.Mac);
        Assert.DoesNotContain(books, s => s.Text.Contains("Ctrl+", StringComparison.Ordinal) || s.Text.Contains("right-click", StringComparison.Ordinal));
        Assert.Contains(books, s => s.Text.Contains("⌘F", StringComparison.Ordinal));
    }
}
