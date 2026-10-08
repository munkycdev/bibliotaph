using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Bibliotaph.Index;

namespace Bibliotaph.App.Controls;

/// <summary>
/// Shows a search snippet in a TextBlock with its hits marked, after the mockup's &lt;mark&gt;. Hits arrive between
/// <see cref="LibraryQueries.HitStart"/> and <see cref="LibraryQueries.HitEnd"/>; line breaks become spaces.
/// </summary>
public static class Highlight
{
    public static readonly DependencyProperty SnippetProperty = DependencyProperty.RegisterAttached(
        "Snippet", typeof(string), typeof(Highlight), new PropertyMetadata(null, OnSnippetChanged));

    public static string? GetSnippet(DependencyObject element) => (string?)element.GetValue(SnippetProperty);
    public static void SetSnippet(DependencyObject element, string? value) => element.SetValue(SnippetProperty, value);

    static void OnSnippetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock block) return;
        block.Inlines.Clear();
        var text = Flatten((string?)e.NewValue ?? "");
        var marked = false;
        foreach (var part in text.Split(LibraryQueries.HitStart, LibraryQueries.HitEnd))
        {
            if (part.Length > 0)
            {
                var run = new Run(part);
                if (marked)
                {
                    run.FontWeight = FontWeights.SemiBold;
                    run.SetResourceReference(TextElement.BackgroundProperty, "Bt.Warm");
                    run.SetResourceReference(TextElement.ForegroundProperty, "Bt.Text");
                }
                block.Inlines.Add(run);
            }
            marked = !marked;
        }
    }

    /// <summary>One line of text: whitespace runs collapse to a space, other control characters go, the markers stay.</summary>
    static string Flatten(string text)
    {
        var builder = new StringBuilder(text.Length);
        var space = false;
        foreach (var c in text)
        {
            if (c is LibraryQueries.HitStart or LibraryQueries.HitEnd)
            {
                // A space before a hit belongs outside the mark.
                if (c == LibraryQueries.HitStart && space) builder.Append(' ');
                if (c == LibraryQueries.HitStart) space = false;
                builder.Append(c);
                continue;
            }
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                space = builder.Length > 0;
                continue;
            }
            if (space) builder.Append(' ');
            space = false;
            builder.Append(c);
        }
        return builder.ToString();
    }
}
