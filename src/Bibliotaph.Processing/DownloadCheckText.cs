namespace Bibliotaph.Processing;

/// <summary>Check a download's words, in the Windows app and on the web: each verdict's heading and what it means, and each file's line.</summary>
public static class DownloadCheckText
{
    public static string Label(CheckVerdict verdict) => verdict switch
    {
        CheckVerdict.New => "New",
        CheckVerdict.MaybeNewVersion => "Maybe a new version",
        CheckVerdict.OwnedElsewhere => "Owned elsewhere",
        CheckVerdict.AnotherCopy => "Another copy",
        CheckVerdict.AlreadyOwned => "Already owned",
        _ => "Couldn't be read",
    };

    public static string Explanation(CheckVerdict verdict) => verdict switch
    {
        CheckVerdict.New => "Not in your library.",
        CheckVerdict.MaybeNewVersion => "Shares most pages with a book you have, or its title and publisher. It may be an updated printing.",
        CheckVerdict.OwnedElsewhere => "Has the title of a book you own in print or on a virtual tabletop.",
        CheckVerdict.AnotherCopy => "The same book page for page, perhaps watermarked for someone else.",
        CheckVerdict.AlreadyOwned => "The very same file is in your library.",
        _ => "These files couldn't be checked.",
    };

    /// <summary>"Another copy of Drowned Abbey", "In your library at …", and anything to add, such as why its pages weren't read.</summary>
    public static string Detail(CheckedFile result)
    {
        var title = result.MatchTitle ?? "a book in your library";
        var said = result.Verdict switch
        {
            CheckVerdict.AlreadyOwned => result.MatchWhere is { } where ? $"{title}, at {where}" : title,
            CheckVerdict.AnotherCopy => $"Another copy of {title}",
            CheckVerdict.MaybeNewVersion => $"Maybe a new version of {title}",
            CheckVerdict.OwnedElsewhere => $"You own {title} elsewhere",
            _ => null,
        };
        return string.Join(" ", new[] { said is null ? null : said + ".", result.Note }.OfType<string>());
    }
}
