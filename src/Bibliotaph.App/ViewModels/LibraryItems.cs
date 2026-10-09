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

    public long DocumentId => Entry.DocumentId;

    public string Title => Entry.Title;

    /// <summary>
    /// "D&amp;D 5e · Adventure" once there is metadata, otherwise "PDF · 320 pages" or "PNG image"; with "still being
    /// read" until its text is searchable.
    /// </summary>
    public string Meta => (Catalogued ?? Describe(Entry)) + (Entry.Searchable ? "" : " · still being read");

    string? Catalogued => Entry.System is null && Entry.Kind is null ? null : string.Join(" · ", new[] { Entry.System, Entry.Kind }.OfType<string>());

    public string SystemLabel => Entry.System ?? "";

    public string KindLabel => Entry.Kind ?? "";

    public string LevelsLabel => Entry.Levels ?? "";

    public string PublisherLabel => Entry.Publisher ?? "";

    /// <summary>The publisher, or the folders when there isn't one, under the title in the list.</summary>
    public string Byline => Entry.Publisher ?? Entry.FolderHint ?? "";

    /// <summary>Pages for a PDF, the format for an image.</summary>
    public string SizeLabel => PagesLabel.Length > 0 ? PagesLabel : FormatLabel;

    public string Folder => Entry.FolderHint ?? "";

    public string FormatLabel => Entry.Format.ToUpperInvariant();

    /// <summary>A model has read this book: its cover carries a spark.</summary>
    public bool IsAiRead => Entry.AiModel is not null;

    public string? AiTip => Entry.AiModel is { } model ? $"Catalogued with AI ({model})" : null;

    public string PagesLabel => Entry.PageCount is { } pages ? $"{pages.ToString("N0", CultureInfo.CurrentCulture)} pp." : "";

    /// <summary>The first letter of the title, for the placeholder cover.</summary>
    public string Initial => Title.FirstOrDefault(char.IsLetterOrDigit) is var c and not '\0' ? char.ToUpper(c, CultureInfo.CurrentCulture).ToString() : "?";

    public bool HasCover => Cover is not null;

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

    /// <summary>Takes newer facts about the same document, such as a cover that has just been made.</summary>
    public void Update(LibraryEntry entry)
    {
        if (entry == Entry) return;
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
        OnPropertyChanged(nameof(Initial));
        OnPropertyChanged(nameof(IsAiRead));
        OnPropertyChanged(nameof(AiTip));
    }

    public static string Describe(LibraryEntry entry) =>
        SourceFormats.IsImage(entry.Format) ? $"{entry.Format.ToUpperInvariant()} image"
        : entry.PageCount is { } pages ? $"PDF · {pages.ToString("N0", CultureInfo.CurrentCulture)} {(pages == 1 ? "page" : "pages")}"
        : "PDF";
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
