using System.Globalization;
using Bibliotaph.Index;

namespace Bibliotaph.Processing;

/// <summary>
/// What Ask says as it answers a question from the library, kept here so the Windows app and the web app say it in
/// the same words. What it shares with Ask this book (the model's steps, an answer that doesn't answer) is in
/// <see cref="AskText"/>.
/// </summary>
public static class LibraryAskText
{
    public const string Looking = "Looking through your books…";

    /// <summary>"The Sunken Keep · p. 112 · The Mill", or without the bookmark for a page under none.</summary>
    public static string Link(LibraryAskPassage passage) => $"{passage.Book} · p. {passage.Page}" + (passage.Heading is { } heading ? $" · {heading}" : "");

    /// <summary>"Found by its words", "by its meaning", or both.</summary>
    public static string Found(LibraryAskPassage passage) => (passage.ByWords, passage.ByMeaning) switch
    {
        (true, true) => "Found by its words and its meaning",
        (false, true) => "Found by its meaning",
        _ => "Found by its words",
    };

    /// <summary>For a screen reader: "Open The Sunken Keep at page 112".</summary>
    public static string Open(LibraryAskPassage passage) => $"Open {passage.Book} at page {passage.Page}";

    /// <summary>Over the pages once answered from them: "Read from 4 pages in 3 books".</summary>
    public static string ReadFrom(LibraryAskFindings findings)
    {
        var books = findings.Evidence.Select(e => e.DocumentId).Distinct().Count();
        return $"Read from {Count(findings.Evidence.Count)} {(findings.Evidence.Count == 1 ? "page" : "pages")} in {Count(books)} {(books == 1 ? "book" : "books")}";
    }

    /// <summary>Why there is nothing to answer from: nothing looks like it's about the question. Null when something does.</summary>
    public static string? Weak(LibraryAskFindings findings) => findings.Weak
        ? "Nothing in these books looks like it's about that, so there's no answer to give." +
          (findings.LookedAt.Count > 0 ? $" These pages came closest: {string.Join("; ", findings.LookedAt)}." : "")
        : null;

    /// <summary>How much was searched, and whether by meaning as well as by words.</summary>
    public static string Coverage(LibraryAskFindings findings)
    {
        var coverage = findings.Coverage;
        if (coverage.Books == 0) return "None of these books has pages with text yet.";
        var searched = $"Searched {Count(coverage.PagesWithText)} pages in {Count(coverage.Books)} {(coverage.Books == 1 ? "book" : "books")}";
        return findings.Meaning switch
        {
            AskMeaning.Off => searched + " by their words. Choose an embedding model in Settings > AI to find pages by meaning too.",
            _ when findings.MeaningProblem is { } problem => searched + $" by their words alone this time: {problem}",
            AskMeaning.On => searched + " by their words and their meaning.",
            _ => searched + $" by their words, and {Count(coverage.PagesWithVectors)} of them by their meaning too; the rest are read for meaning in the background.",
        };
    }

    static string Count(int count) => count.ToString("N0", CultureInfo.CurrentCulture);
}
