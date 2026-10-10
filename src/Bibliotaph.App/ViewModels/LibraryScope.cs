using Bibliotaph.Core;

namespace Bibliotaph.App.ViewModels;

/// <summary>
/// A part of the library the Library page can show on its own (slice 3 plan, choice 3): Favorites now, a collection
/// or a Smart View later. <see cref="Key"/> is its rows' scope in index.db's entry_scope. The page names it in its
/// heading and in a chip that clears it, so a scoped Library never passes for the whole one.
/// </summary>
public sealed record LibraryScope(string Key, string Name, string Subtitle, string EmptyTitle, string EmptyMessage)
{
    public static LibraryScope Favorites { get; } = new(ScopeKeys.Favorites, "Favorites", "The books you marked with a heart.",
        "No favorites yet.", "Mark a book with the heart on its cover, in its details or in the reader, and it shows here.");
}
