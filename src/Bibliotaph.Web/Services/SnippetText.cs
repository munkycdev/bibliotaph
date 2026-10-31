using Bibliotaph.Api.Contracts;

namespace Bibliotaph.Web.Services;

/// <summary>A matching page's snippet as the page marks it.</summary>
public static class SnippetText
{
    /// <summary>The snippet's text, split where its hits start and end.</summary>
    public static IEnumerable<(string Text, bool Hit)> Parts(string snippet)
    {
        var hit = false;
        var start = 0;
        for (var i = 0; i <= snippet.Length; i++)
        {
            if (i < snippet.Length && snippet[i] != LibraryChoices.HitStart && snippet[i] != LibraryChoices.HitEnd) continue;
            if (i > start) yield return (snippet[start..i], hit);
            if (i < snippet.Length) hit = snippet[i] == LibraryChoices.HitStart;
            start = i + 1;
        }
    }
}
