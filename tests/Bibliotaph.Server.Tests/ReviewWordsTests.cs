using Bibliotaph.Api.Contracts;
using Bibliotaph.Processing;

namespace Bibliotaph.Server.Tests;

/// <summary>The web page works out its counting words itself, so they must stay the Windows app's.</summary>
public sealed class ReviewWordsTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(0, 3)]
    [InlineData(1, 1)]
    [InlineData(1234, 2)]
    public void Needs_reviews_counting_words_are_the_windows_apps(int remaining, int files)
    {
        Assert.Equal(ReviewText.Subtitle(remaining, files), ReviewWords.Subtitle(remaining, files));
        Assert.Equal(ReviewText.SuggestionsTab(remaining), ReviewWords.SuggestionsTab(remaining));
        Assert.Equal(ReviewText.FilesTab(files), ReviewWords.FilesTab(files));
        Assert.Equal(ReviewText.ShowLabel("Game system", remaining), ReviewWords.ShowLabel("Game system", remaining));
        Assert.Equal(ReviewText.MoreLabel(remaining), ReviewWords.MoreLabel(remaining));
        Assert.Equal(ReviewText.ShowFiles(files, remaining > 0), ReviewWords.ShowFiles(files, remaining > 0));
        Assert.Equal(ReviewText.PageSize, ReviewWords.PageSize);
    }
}
