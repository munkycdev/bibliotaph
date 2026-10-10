using System.Globalization;
using System.Windows.Media;
using Bibliotaph.App.Services;
using Bibliotaph.Core;
using Bibliotaph.Index;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Bibliotaph.App.ViewModels;

/// <summary>A book or image in the grid, list or results. Its cover loads the first time a cell asks for it.</summary>
public sealed class LibraryItemViewModel(LibraryEntry entry, CoverImages covers) : ObservableObject
{
    bool _coverRequested;

    public LibraryEntry Entry { get; private set; } = entry;

    public EntryId EntryId => Entry.EntryId;

    /// <summary>The document the card shows, which opens; 0 for a book owned elsewhere.</summary>
    public long DocumentId => Entry.DocumentId;

    /// <summary>
    /// A book owned elsewhere, with no file here (F5 plan, choice 4): a placeholder cover with an "Elsewhere" mark,
    /// and nothing to open.
    /// </summary>
    public bool IsElsewhere => Entry.IsElsewhere;

    /// <summary>The card has a file to open.</summary>
    public bool CanOpen => !IsElsewhere;

    public string Title => Entry.Title;

    /// <summary>
    /// "D&amp;D 5e · Adventure" once there is metadata, otherwise "PDF · 320 pages" or "PNG image"; with "still being
    /// read" until its text is searchable. A pack a search found by one of its images names that image (F4 plan, choice 8).
    /// </summary>
    public string Meta => Entry.MatchedMember ?? (Catalogued ?? Describe(Entry)) + (Entry.Searchable || IsElsewhere ? "" : " · still being read");

    string? Catalogued => Entry.System is null && Entry.Kind is null ? null : string.Join(" · ", new[] { Entry.System, Entry.Kind }.OfType<string>());

    public string SystemLabel => Entry.System ?? "";

    public string KindLabel => Entry.Kind ?? "";

    public string LevelsLabel => Entry.Levels ?? "";

    public string PublisherLabel => Entry.Publisher ?? "";

    /// <summary>
    /// The publisher, or the folders when there isn't one, under the title in the list; for a pack a search found by an
    /// image, that image's name.
    /// </summary>
    public string Byline => Entry.MatchedMember ?? Entry.Publisher ?? Entry.FolderHint ?? "";

    /// <summary>Pages for a PDF, the format for an image, "120 images" for a pack, "Print · Foundry VTT" for a book owned elsewhere.</summary>
    public string SizeLabel => IsPack ? ImagesLabel : IsElsewhere ? Entry.AlsoOwn ?? "Elsewhere" : PagesLabel.Length > 0 ? PagesLabel : FormatLabel;

    /// <summary>Many images shown as one card (F4): its cover is a mosaic of its first four.</summary>
    public bool IsPack => Entry.IsPack;

    /// <summary>"120 images", for a pack.</summary>
    public string ImagesLabel => IsPack ? Images(Entry.Members) : "";

    /// <summary>The covers of a pack's first four images, for its mosaic. Each loads the first time a cell asks for it.</summary>
    public IReadOnlyList<CoverTile> Mosaic
    {
        get
        {
            if (field is null || !field.Select(t => t.Name).SequenceEqual(Entry.MosaicCovers))
                field = [.. Entry.MosaicCovers.Select(name => new CoverTile(name, covers))];
            return field;
        }
    }

    public string Folder => Entry.FolderHint ?? "";

    public string FormatLabel => IsPack ? "PACK" : IsElsewhere ? "ELSEWHERE" : Entry.Format.ToUpperInvariant();

    /// <summary>A model has read this book: its cover carries a spark.</summary>
    public bool IsAiRead => Entry.AiModel is not null;

    public string? AiTip => Entry.AiModel is { } model ? $"Catalogued with AI ({model})" : null;

    /// <summary>"2 copies" when the book is in more than one file (F2), for the detail list; empty otherwise.</summary>
    public string CopiesLabel => Entry.Copies > 1 ? $"{Entry.Copies.ToString("N0", CultureInfo.CurrentCulture)} copies" : "";

    public string PagesLabel => Entry.PageCount is { } pages ? $"{pages.ToString("N0", CultureInfo.CurrentCulture)} pp." : "";

    /// <summary>The first letter of the title, for the placeholder cover.</summary>
    public string Initial => Title.FirstOrDefault(char.IsLetterOrDigit) is var c and not '\0' ? char.ToUpper(c, CultureInfo.CurrentCulture).ToString() : "?";

    public bool HasCover => Cover is not null;

    bool? _favorite;

    /// <summary>
    /// Marked with a heart (slice 3 plan, choice 5). A click shows the change at once; the next list from the index
    /// that agrees with it takes over.
    /// </summary>
    public bool IsFavorite => _favorite ?? Entry.Favorite;

    /// <summary>What the heart does, for its tooltip and screen readers.</summary>
    public string FavoriteAction => IsFavorite ? "Remove from favorites" : "Add to favorites";

    /// <summary>Shows the heart as set or not before the index has caught up.</summary>
    public void ShowFavorite(bool favorite)
    {
        _favorite = favorite;
        OnPropertyChanged(nameof(IsFavorite));
        OnPropertyChanged(nameof(FavoriteAction));
    }

    /// <summary>When this book was last opened, for Home; null if never.</summary>
    public DateTime? OpenedUtc => Entry.OpenedUtc;

    /// <summary>Ticked in the Library's Select mode. <see cref="BookSelection"/> sets it.</summary>
    public bool IsSelected
    {
        get;
        internal set => SetProperty(ref field, value);
    }

    public ImageSource? Cover
    {
        get
        {
            if (!_coverRequested && Entry.Cover is { } name)
            {
                _coverRequested = true;
                field = covers.TryGet(name);
                if (field is null) _ = LoadCoverAsync(name);
            }
            return field;
        }
        private set
        {
            field = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasCover));
        }
    }

    async Task LoadCoverAsync(string name)
    {
        var image = await covers.LoadAsync(name);
        if (image is not null) Cover = image;
    }

    /// <summary>Takes newer facts about the same entry, such as a cover that has just been made or another copy made current.</summary>
    public void Update(LibraryEntry entry)
    {
        if (entry == Entry) return;
        if (_favorite == entry.Favorite) _favorite = null;
        var coverChanged = entry.Cover != Entry.Cover;
        Entry = entry;
        if (coverChanged)
        {
            _coverRequested = false;
            Cover = null;
        }
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Meta));
        OnPropertyChanged(nameof(Folder));
        OnPropertyChanged(nameof(PagesLabel));
        OnPropertyChanged(nameof(SystemLabel));
        OnPropertyChanged(nameof(KindLabel));
        OnPropertyChanged(nameof(LevelsLabel));
        OnPropertyChanged(nameof(PublisherLabel));
        OnPropertyChanged(nameof(Byline));
        OnPropertyChanged(nameof(SizeLabel));
        OnPropertyChanged(nameof(CopiesLabel));
        OnPropertyChanged(nameof(Initial));
        OnPropertyChanged(nameof(IsAiRead));
        OnPropertyChanged(nameof(AiTip));
        OnPropertyChanged(nameof(ImagesLabel));
        OnPropertyChanged(nameof(Mosaic));
        OnPropertyChanged(nameof(IsFavorite));
        OnPropertyChanged(nameof(FavoriteAction));
        OnPropertyChanged(nameof(OpenedUtc));
    }

    public static string Images(int count) => $"{count.ToString("N0", CultureInfo.CurrentCulture)} {(count == 1 ? "image" : "images")}";

    public static string Describe(LibraryEntry entry) =>
        entry.IsPack ? $"Image pack · {Images(entry.Members)}"
        : entry.IsElsewhere ? entry.AlsoOwn is { } owned ? $"Owned elsewhere · {owned}" : "Owned elsewhere"
        : SourceFormats.IsImage(entry.Format) ? $"{entry.Format.ToUpperInvariant()} image"
        : entry.PageCount is { } pages ? $"PDF · {pages.ToString("N0", CultureInfo.CurrentCulture)} {(pages == 1 ? "page" : "pages")}"
        : "PDF";
}

/// <summary>One image's cover in a pack's mosaic or grid, loaded the first time it is asked for.</summary>
public sealed class CoverTile(string name, CoverImages covers) : ObservableObject
{
    bool _requested;

    public string Name { get; } = name;

    public ImageSource? Cover
    {
        get
        {
            if (!_requested)
            {
                _requested = true;
                field = covers.TryGet(Name);
                if (field is null) _ = LoadAsync();
            }
            return field;
        }
        private set => SetProperty(ref field, value);
    }

    async Task LoadAsync()
    {
        if (await covers.LoadAsync(Name) is { } image) Cover = image;
    }
}

/// <summary>An image in a pack's inspector grid: its name and cover. Clicking it opens it in the viewer.</summary>
public sealed class PackImageViewModel(PackImage image, CoverImages covers)
{
    public PackImage Image { get; } = image;

    public string Name => Image.Name;

    public CoverTile? Tile { get; } = image.Cover is { } cover ? new CoverTile(cover, covers) : null;
}

/// <summary>A page that matched a search, quoted around the hit.</summary>
public sealed class PageHitViewModel(PageHit hit)
{
    public PageHit Hit { get; } = hit;

    /// <summary>Text with hits between <see cref="LibraryQueries.HitStart"/> and <see cref="LibraryQueries.HitEnd"/>.</summary>
    public string Snippet => Hit.Snippet;

    /// <summary>"Printed p. 12 · PDF page 16", or just the PDF page when the file has no labels of its own (A03).</summary>
    public string Where
    {
        get
        {
            var pdfPage = (Hit.PdfPage + 1).ToString("N0", CultureInfo.CurrentCulture);
            return string.IsNullOrWhiteSpace(Hit.Label) || Hit.Label == pdfPage
                ? $"PDF page {pdfPage}"
                : $"Printed p. {Hit.Label} · PDF page {pdfPage}";
        }
    }

    public string Source => Hit.FromOcr ? "Read from a scan" : "Text match";
}

/// <summary>A book's best matching pages.</summary>
public sealed class DocumentHitsViewModel(LibraryItemViewModel item, IReadOnlyList<PageHitViewModel> pages, long matchingPages)
{
    public LibraryItemViewModel Item { get; } = item;

    public IReadOnlyList<PageHitViewModel> Pages { get; } = pages;

    /// <summary>"3 more pages match", when more matched than are shown.</summary>
    public string More
    {
        get
        {
            var more = matchingPages - Pages.Count;
            return more <= 0 ? "" : $"{more.ToString("N0", CultureInfo.CurrentCulture)} more {(more == 1 ? "page matches" : "pages match")}";
        }
    }

    public bool HasMore => More.Length > 0;
}

/// <summary>An option in a filter or sort menu.</summary>
public sealed record Choice<T>(T Value, string Label)
{
    public override string ToString() => Label;
}
