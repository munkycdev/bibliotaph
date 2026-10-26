namespace Bibliotaph.Core.Search;

/// <summary>
/// What the Ask panel beside the search box says, kept here so the Windows app and the web app, which both show it,
/// say it in the same words.
/// </summary>
public static class AskPanelText
{
    /// <summary>The search box's hint while Ask is on.</summary>
    public const string Placeholder = "Ask for books in plain English…";

    public const string Intro = "Describe what you're looking for, such as “a one-shot heist for 4 players in a city”, and press Enter to search. "
        + "Or ask your books a question, such as “how does grappling work?”, and press Ctrl+Enter or Answer from my books.";

    public const string NeedsAi = "Ask needs AI to read your question. Set AI up in Settings > AI, or close Ask and search as usual.";
    public const string Reading = "Reading your question…";
    public const string NothingToSearch = "Nothing in that could be searched for. Try saying it another way.";
    public const string Interpreted = "Here's how that reads as a search. Change or remove anything, then press Search.";
    public const string Failed = "Something went wrong reading your question. Try again.";
    public const string QuestionFirst = "Type a question first, then press Ctrl+Enter.";

    /// <summary>Which books a question goes to: "Answers come from the books the Library shows: D&amp;D 5e, Adventure."</summary>
    public static string Scope(IReadOnlyList<string> labels) => labels.Count > 0
        ? $"Answers come from the books the Library shows: {string.Join(", ", labels)}."
        : "Answers come from every book in your library. Filter the Library first to ask fewer.";
}
