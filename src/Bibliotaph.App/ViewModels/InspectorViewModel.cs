using System.Diagnostics;
using System.Globalization;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Bibliotaph.Index;
using Bibliotaph.Processing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bibliotaph.App.ViewModels;

public sealed record InspectorFact(string Label, string Value);

/// <summary>
/// Where a document's file is, and whether it can be read now. The first place carries the book's Reprocess button
/// beside its Show in File Explorer: copies are one book, so reprocessing one covers them all.
/// </summary>
public sealed record InspectorLocation(string Path, string State, bool ShowsReprocess = false)
{
    public bool HasState => State.Length > 0;
}

public sealed record InspectorStage(string Name, string Status);

/// <summary>
/// The inspector: a document's metadata, with where each value came from and the user's corrections (Details), then
/// what Bibliotaph knows from its file (pages, capabilities, where it is, how far indexing got). Reprocess reads the
/// file again; the processing list follows its stages as they run.
/// </summary>
public sealed partial class InspectorViewModel : ObservableObject, IMetadataEditor
{
    /// <summary>The fields shown even when nothing is known about them.</summary>
    static readonly HashSet<MetadataField> PrimaryFields =
        [MetadataFields.Title, MetadataFields.System, MetadataFields.Edition, MetadataFields.Types, MetadataFields.Levels, MetadataFields.Publisher];

    readonly MetadataService _metadata;
    readonly LibraryQueries _queries;
    readonly IndexingService _indexing;
    bool _refreshing;
    bool _refreshAgain;

    InspectorViewModel(LibraryItemViewModel item, DocumentDetails? details, IReadOnlyList<DocumentLocation> locations, MetadataService metadata,
        LibraryQueries queries, IndexingService indexing)
    {
        Item = item;
        _metadata = metadata;
        _queries = queries;
        _indexing = indexing;
        Locations = [.. locations.Select((l, i) => new InspectorLocation(l.FullPath, LocationState(l), ShowsReprocess: i == 0))];
        ShowProcessing(details?.Entry ?? item.Entry, details);
    }

    /// <summary>Raised after the user changed this document's metadata, so the library can show it.</summary>
    public event EventHandler? MetadataChanged;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VisibleFields), nameof(HasHiddenFields))]
    public partial IReadOnlyList<MetadataFieldViewModel> Fields { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VisibleFields), nameof(HasHiddenFields), nameof(MoreFieldsLabel))]
    public partial bool ShowAllFields { get; set; }

    public IReadOnlyList<MetadataFieldViewModel> VisibleFields => [.. Fields.Where(f => ShowAllFields || f.IsKnown || f.IsPrimary)];

    public bool HasHiddenFields => Fields.Any(f => !f.IsKnown && !f.IsPrimary);

    public string MoreFieldsLabel => ShowAllFields ? "Fewer fields" : "More fields";

    [RelayCommand]
    void ToggleAllFields() => ShowAllFields = !ShowAllFields;

    async Task ReloadMetadataAsync(bool changed)
    {
        var (metadata, vocabulary) = await _metadata.GetAsync(Item.DocumentId);
        var open = Fields.Where(f => f.ShowEvidence).Select(f => f.Field).ToHashSet();
        Fields = [.. metadata.Fields.Select(f => new MetadataFieldViewModel(f, vocabulary, this, PrimaryFields.Contains(f.Field))
        {
            ShowEvidence = open.Contains(f.Field),
        })];
        if (changed) MetadataChanged?.Invoke(this, EventArgs.Empty);
    }

    async Task<string?> IMetadataEditor.SaveAsync(MetadataField field, string typed)
    {
        var problem = await Task.Run(() => _metadata.SetAsync(Item.DocumentId, field, typed));
        if (problem is not null) return problem.Message;
        await ReloadMetadataAsync(changed: true);
        return null;
    }

    async Task IMetadataEditor.KeepAsync(MetadataField field)
    {
        await Task.Run(() => _metadata.ConfirmAsync(Item.DocumentId, field));
        await ReloadMetadataAsync(changed: true);
    }

    async Task IMetadataEditor.RejectAsync(MetadataField field, string normalized)
    {
        await Task.Run(() => _metadata.RejectAsync(Item.DocumentId, field, normalized));
        await ReloadMetadataAsync(changed: true);
    }

    async Task IMetadataEditor.UseAsync(MetadataField field, string value)
    {
        await Task.Run(() => _metadata.UseAsync(Item.DocumentId, field, value));
        await ReloadMetadataAsync(changed: true);
    }

    async Task IMetadataEditor.ResetAsync(MetadataField field)
    {
        await Task.Run(() => _metadata.ResetAsync(Item.DocumentId, field));
        await ReloadMetadataAsync(changed: true);
    }

    public LibraryItemViewModel Item { get; }

    public string Title => Item.Title;

    public string Eyebrow => Item.Entry.Format == SourceFormats.Pdf ? "PDF" : "IMAGE";

    [ObservableProperty]
    public partial IReadOnlyList<InspectorFact> Facts { get; private set; } = [];

    public IReadOnlyList<InspectorLocation> Locations { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStages))]
    public partial IReadOnlyList<InspectorStage> Stages { get; private set; } = [];

    public bool HasStages => Stages.Count > 0;

    /// <summary>
    /// True while any of the book's stages is waiting or running, as after Reprocess: the button reads
    /// "Reprocessing…" and is disabled until every stage has finished, failed or been blocked.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReprocessLabel), nameof(CanReprocess))]
    [NotifyCanExecuteChangedFor(nameof(ReprocessCommand), nameof(ReprocessWithOcrCommand))]
    public partial bool IsReprocessing { get; private set; }

    public bool CanReprocess => !IsReprocessing;

    public string ReprocessLabel => IsReprocessing ? "Reprocessing…" : "Reprocess";

    /// <summary>The PDF's page count, for Reprocess with OCR on every page; 0 for an image or a PDF not yet opened.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOcrEveryPage))]
    public partial int PageCount { get; private set; }

    public bool CanOcrEveryPage => PageCount > 0;

    public static async Task<InspectorViewModel> LoadAsync(LibraryItemViewModel item, LibraryQueries queries, LibraryStore library, MetadataService metadata,
        IndexingService indexing)
    {
        var details = await Task.Run(() => queries.GetDetailsAsync(item.DocumentId));
        var locations = await library.GetLocationsAsync(item.DocumentId);
        var inspector = new InspectorViewModel(item, details, locations, metadata, queries, indexing);
        await inspector.ReloadMetadataAsync(changed: false);
        return inspector;
    }

    /// <summary>
    /// Reads the file again: pages, text, cover and hints from names, and OCR of the pages that need it. Everything
    /// the user added stays. It also serves as a retry for a stage that failed or is blocked.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanReprocess))]
    Task Reprocess() => StartReprocessAsync(ocrEveryPage: false);

    /// <summary>Reprocess, with every page read by OCR, for a weak text layer that passes the garbage check. Asks first.</summary>
    [RelayCommand(CanExecute = nameof(CanReprocess))]
    async Task ReprocessWithOcr()
    {
        if (PageCount == 0 || !Services.ReprocessPrompt.ConfirmOcrEveryPage(Title, PageCount)) return;
        await StartReprocessAsync(ocrEveryPage: true);
    }

    async Task StartReprocessAsync(bool ocrEveryPage)
    {
        if (await Task.Run(() => _indexing.ReprocessAsync(Item.DocumentId, ocrEveryPage))) IsReprocessing = true;
        await RefreshAsync();
    }

    /// <summary>
    /// Reads again how far the book's processing has got, as each indexing refresh does, so the processing list and
    /// the Reprocess button follow its stages. A call made while one is reading runs again once it is done.
    /// </summary>
    public async Task RefreshAsync()
    {
        if (_refreshing)
        {
            _refreshAgain = true;
            return;
        }
        _refreshing = true;
        try
        {
            do
            {
                _refreshAgain = false;
                var wasProcessing = IsReprocessing;
                var details = await Task.Run(() => _queries.GetDetailsAsync(Item.DocumentId));
                ShowProcessing(details?.Entry ?? Item.Entry, details);
                // Hints from names may have changed; a field being edited is left alone.
                if (wasProcessing && !IsReprocessing && !Fields.Any(f => f.IsEditing)) await ReloadMetadataAsync(changed: false);
            }
            while (_refreshAgain);
        }
        finally
        {
            _refreshing = false;
        }
    }

    void ShowProcessing(LibraryEntry entry, DocumentDetails? details)
    {
        var facts = BuildFacts(entry, details);
        if (!facts.SequenceEqual(Facts)) Facts = facts;
        IReadOnlyList<InspectorStage> stages = details is null ? [] : [.. details.Stages.Select(s => new InspectorStage(StageName(s.Stage), StatusText(s)))];
        if (!stages.SequenceEqual(Stages)) Stages = stages;
        IsReprocessing = details?.Stages.Any(s => s.Status is StageStatus.Pending or StageStatus.Running) == true;
        PageCount = entry.Format == SourceFormats.Pdf ? entry.PageCount ?? 0 : 0;
    }

    /// <summary>Opens File Explorer with the file selected. Explorer only shows it; nothing is changed.</summary>
    [RelayCommand]
    static void ShowInExplorer(InspectorLocation location) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{location.Path}\"") { UseShellExecute = false });

    static List<InspectorFact> BuildFacts(LibraryEntry entry, DocumentDetails? details)
    {
        var facts = new List<InspectorFact>
        {
            new("Format", details is { WidthPx: { } w, HeightPx: { } h }
                ? $"{LibraryItemViewModel.Describe(entry)} · {w.ToString("N0", CultureInfo.CurrentCulture)} × {h.ToString("N0", CultureInfo.CurrentCulture)} px"
                : LibraryItemViewModel.Describe(entry)),
            new("Added", entry.AddedUtc.ToLocalTime().ToString("d MMMM yyyy", CultureInfo.CurrentCulture)),
        };
        if (!string.IsNullOrEmpty(entry.FolderHint)) facts.Add(new("Folders", entry.FolderHint));
        if (details is null) return facts;

        facts.Add(new("Search", SearchText(entry, details)));
        if (details.Encrypted) facts.Add(new("Password", "Needs a password to open"));
        if (entry.Format == SourceFormats.Pdf) facts.Add(new("Copying text", details.CanCopy ? "Allowed" : "Not allowed by the file"));
        // The PDF's own information is often wrong ("Microsoft Word - final2.doc"), so it is labelled as the file's.
        if (!string.IsNullOrWhiteSpace(details.MetaTitle)) facts.Add(new("Title in the file", details.MetaTitle));
        if (!string.IsNullOrWhiteSpace(details.MetaAuthor)) facts.Add(new("Author in the file", details.MetaAuthor));
        if (!string.IsNullOrWhiteSpace(details.MetaSubject)) facts.Add(new("Subject in the file", details.MetaSubject));
        return facts;
    }

    static string SearchText(LibraryEntry entry, DocumentDetails details)
    {
        if (SourceFormats.IsImage(entry.Format)) return "Found by its name";
        if (!entry.Searchable) return "Its text is still being read";
        var text = details.OcrPages > 0
            ? $"Text searchable, {details.OcrPages.ToString("N0", CultureInfo.CurrentCulture)} scanned {(details.OcrPages == 1 ? "page" : "pages")} read"
            : "Text searchable";
        return details.PagesAwaitingOcr > 0
            ? $"{text}; {details.PagesAwaitingOcr.ToString("N0", CultureInfo.CurrentCulture)} scanned {(details.PagesAwaitingOcr == 1 ? "page" : "pages")} still to read"
            : text;
    }

    static string LocationState(DocumentLocation location) => (location.State, location.RootAvailability) switch
    {
        (_, SourceRootAvailability.Offline) => "Folder can't be reached right now",
        (FileLocationState.Missing, _) => "No longer here",
        (FileLocationState.OnlineOnly, _) => "Online-only",
        _ => "",
    };

    static string StageName(Stage stage) => stage switch
    {
        Stage.Probe => "Opening",
        Stage.Text => "Reading text",
        Stage.Covers => "Cover",
        Stage.RuleHints => "Hints from names",
        Stage.Ocr => "Reading scanned pages",
        _ => stage.ToString(),
    };

    static string StatusText(StageState stage) => stage.Status switch
    {
        StageStatus.Complete => "Done",
        StageStatus.Partial => stage.Reason is null ? "Partly done" : $"Partly done: {stage.Reason}",
        StageStatus.Skipped => "Not needed",
        StageStatus.Pending => "Waiting",
        StageStatus.Running => "Working on it…",
        _ => stage.Reason ?? stage.Status.ToString(),
    };
}
