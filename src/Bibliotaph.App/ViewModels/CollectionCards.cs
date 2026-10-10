using System.Windows;
using Bibliotaph.Catalog;

namespace Bibliotaph.App.ViewModels;

/// <summary>
/// A collection's card (slice 3 plan, choice 9): a mosaic of the covers of the two books added last, its name, and how
/// many books and sub-collections it holds. A click opens the Library scoped to it.
/// </summary>
public sealed class CollectionCardViewModel(CollectionInfo collection, string countLabel, IReadOnlyList<LibraryItemViewModel> covers)
{
    public CollectionInfo Collection { get; } = collection;

    public long Id => Collection.Id;

    public string Name => Collection.Name;

    public bool Pinned => Collection.Pinned;

    /// <summary>"12 books · 2 collections".</summary>
    public string CountLabel { get; } = countLabel;

    public IReadOnlyList<LibraryItemViewModel> Covers { get; } = covers;

    public bool HasCovers => Covers.Count > 0;

    public LibraryItemViewModel? FirstCover => Covers.Count > 0 ? Covers[0] : null;

    public LibraryItemViewModel? SecondCover => Covers.Count > 1 ? Covers[1] : null;

    public bool HasSecondCover => Covers.Count > 1;

    public string Tip => Collection.Description is { } description ? $"{Name}: {description}" : Name;
}

/// <summary>A collection in a picker: indented under the one it sits in, or listed by its path while filtering.</summary>
public sealed record CollectionChoice(long? Id, string Name, string Path, int Depth)
{
    public Thickness Indent => new(Depth * 18, 0, 0, 0);
}
