namespace Bibliotaph.Core;

/// <summary>
/// The names of the groups of entries the Library can be limited to (slice 3 plan, choices 3 and 4): favourites now,
/// collections and session packs as they arrive. catalog.db says which entries are in each; index.db holds a copy in
/// <c>entry_scope</c>, keyed by these names, so library queries can join it.
/// </summary>
public static class ScopeKeys
{
    /// <summary>The books marked with a heart.</summary>
    public const string Favorites = "favorite";
}
