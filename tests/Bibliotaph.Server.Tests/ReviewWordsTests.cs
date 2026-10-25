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
        Assert.Equal(RevisionCardText.Heading("Tomb", remaining, files), ReviewWords.RevisionHeading("Tomb", remaining, files));
        Assert.Equal(RevisionCardText.AllChecked("Tomb"), ReviewWords.AllChecked("Tomb"));
        Assert.Equal(RevisionCardText.FoundHeading, ReviewWords.FoundHeading);
        Assert.Equal(RevisionCardText.UseHint, ReviewWords.UseHint);
        Assert.Equal(ReviewCardText.DismissHint, ReviewWords.DismissHint);
        Assert.Equal(PartProposalCardText.AcceptLabel(remaining), ReviewWords.AcceptParts(remaining));
        Assert.Equal(DetailsText.PagesHint, ReviewWords.PagesHint);
        Assert.Equal((PartProposalCardText.AcceptHint, PartProposalCardText.EditHint, PartProposalCardText.NotSplitHint),
            (ReviewWords.AcceptPartsHint, ReviewWords.EditPartsHint, ReviewWords.NotSplitHint));
    }
}
