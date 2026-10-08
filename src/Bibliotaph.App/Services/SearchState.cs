using Bibliotaph.Core.Search;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Bibliotaph.App.Services;

/// <summary>
/// The search the user typed in the top bar, parsed once and shared: the shell sets it, the library shows its
/// results. Kept apart from the shell so the library doesn't depend on the window.
/// </summary>
public sealed partial class SearchState : ObservableObject
{
    public SearchQuery Query { get; private set; } = SearchQuery.Parse("");

    /// <summary>The text that was last searched for. Raises PropertyChanged after <see cref="Query"/> is updated.</summary>
    [ObservableProperty]
    public partial string Text { get; private set; } = "";

    public bool IsSearching => !string.IsNullOrWhiteSpace(Text);

    public void Search(string text)
    {
        text = text.Trim();
        if (text == Text) return;
        Query = SearchQuery.Parse(text);
        Text = text;
        OnPropertyChanged(nameof(IsSearching));
    }
}
