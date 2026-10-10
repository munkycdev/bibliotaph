using System.Diagnostics;
using System.Globalization;
using Bibliotaph.Catalog;
using Bibliotaph.App.Services;
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
/// beside its Show in File Explorer: copies are one book, so reprocessing one covers them all. For a file inside a ZIP,
/// File Explorer selects the ZIP (<see cref="ExplorerPath"/>). A file that has gone missing offers its last location
/// instead (slice 4g plan, choice 1): the folder it was in, or the nearest one above it that is still there.
/// </summary>
public sealed record InspectorLocation(string Path, string State, bool ShowsReprocess = false, string? ExplorerPath = null, bool IsMissing = false)
{
    public bool HasState => State.Length > 0;

    public string ShowLabel => IsMissing ? "Show last location" : "Show in File Explorer";
}

public sealed record InspectorStage(string Name, string Status);

/// <summary>
/// One file of the book in the inspector's Copies list: "Current copy" or "Copy", or an earlier version whose file
/// has gone, with its pages and where it is.
/// </summary>
public sealed record InspectorCopy(long DocumentId, string Heading, string Detail, string? Path, bool IsCurrent, string? ExplorerPath = null)
{
    public bool HasPath => Path is not null;

    public bool CanMakeCurrent => !IsCurrent;
}

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
    readonly CopiesService _copies;
    readonly PackService _packs;
    readonly ElsewhereService _elsewhere;
    bool _refreshing;
    bool _refreshAgain;

    InspectorViewModel(LibraryItemViewModel item, DocumentDetails? details, IReadOnlyList<InspectorLocation> locations, IReadOnlyList<CopyDetails> copies,
        IReadOnlyList<PackImageViewModel> images, MetadataService metadata, LibraryQueries queries, IndexingService indexing, CopiesService copiesService,
        PackService packs, ElsewhereService elsewhere)
    {
        _elsewhere = elsewhere;
        Item = item;
        _metadata = metadata;
        _queries = queries;
        _indexing = indexing;
        _copies = copiesService;
        _packs = packs;
        Locations = locations;
        Copies = copies.Count < 2 ? [] : [.. copies.Select(ToCopy)];
        PackImages = images;
        ShowProcessing(details?.Entry ?? item.Entry, details);
    }

    /// <summary>A pack's images, for its grid (F4 plan, choice 7); empty for anything else.</summary>
    public IReadOnlyList<PackImageViewModel> PackImages { get; }

    public bool IsPack => Item.IsPack;

    public string PackImagesHeading => $"IMAGES ({PackImages.Count.ToString("N0", CultureInfo.CurrentCulture)})";

    /// <summary>Raised after "Split into separate images", with how many images got their cards back.</summary>
    public event EventHandler<int>? PackSplit;

    /// <summary>"Split into separate images" (choice 4): every image gets its card back, and the folder isn't packed again.</summary>
    [RelayCommand]
    async Task SplitPack()
    {
        if (await Task.Run(() => _packs.SplitAsync(Item.EntryId))) PackSplit?.Invoke(this, PackImages.Count);
    }

    /// <summary>A book owned elsewhere (F5 plan, choice 4): no file, so no Open, locations or processing, and Remove from library.</summary>
    public bool IsElsewhere => Item.IsElsewhere;

    public bool CanOpen => !IsElsewhere;

    public string FactsHeading => IsElsewhere ? "IN YOUR LIBRARY" : "ABOUT THE FILE";

    /// <summary>Raised after "Remove from library", with what Undo needs.</summary>
    public event EventHandler<RemovedEntry>? Removed;

    /// <summary>"Remove from library" (choice 5), for a book owned elsewhere only: a file's card goes when its file does.</summary>
    [RelayCommand]
    async Task RemoveFromLibrary()
    {
        if (await Task.Run(() => _elsewhere.RemoveAsync(Item.EntryId)) is { } removed) Removed?.Invoke(this, removed);
    }

    /// <summary>Raised after the user changed this document's metadata, so the library can show it.</summary>
    public event EventHandler? MetadataChanged;

    /// <summary>
    /// Raised after Make current or Not the same book, with the card to show next: this one, or the copy's own card
    /// once it has been split off.
    /// </summary>
    public event EventHandler<EntryId>? CopiesChanged;

    /// <summary>The book's files when it has more than one (F2): which opens, the others, and earlier versions.</summary>
    public IReadOnlyList<InspectorCopy> Copies { get; }

    public bool HasCopies => Copies.Count > 0;

    public string CopiesHeading => $"COPIES ({Copies.Count.ToString("N0", CultureInfo.CurrentCulture)})";

    /// <summary>Opens this copy instead: its cover, page count and page hits become the card's. Nothing on disk changes.</summary>
    [RelayCommand]
    async Task MakeCurrent(InspectorCopy copy)
    {
        await Task.Run(() => _copies.MakeCurrentAsync(Item.EntryId, copy.DocumentId));
        CopiesChanged?.Invoke(this, Item.EntryId);
    }

    /// <summary>"Not the same book": the copy gets its own card again, and Bibliotaph won't join the two again.</summary>
    [RelayCommand]
    async Task NotSameBook(InspectorCopy copy)
    {
        var card = await Task.Run(() => _copies.NotSameBookAsync(Item.EntryId, copy.DocumentId));
        CopiesChanged?.Invoke(this, card ?? Item.EntryId);
    }

    [RelayCommand]
    static void ShowCopyInExplorer(InspectorCopy copy)
    {
        if ((copy.ExplorerPath ?? copy.Path) is { } path) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });
    }

    static InspectorCopy ToCopy(CopyDetails details)
    {
        var copy = details.Copy;
        var location = details.Locations.FirstOrDefault(l => l.State != FileLocationState.Missing);
        var path = location?.FullPath;
        var heading = copy.IsCurrent ? "Current copy" : path is null ? "Earlier version, no file" : "Copy";
        var parts = new List<string>();
        if (copy.PageCount is { } pages) parts.Add($"{pages.ToString("N0", CultureInfo.CurrentCulture)} {(pages == 1 ? "page" : "pages")}");
        parts.Add($"added {copy.AddedUtc.ToLocalTime().ToString("d MMMM yyyy", CultureInfo.CurrentCulture)}");
        if (copy.Joined) parts.Add("same text, joined automatically");
        if (copy.IsCurrent && !copy.IsShown) parts.Add("its file is missing, so another copy opens");
        return new InspectorCopy(copy.DocumentId, heading, string.Join(" · ", parts), path, copy.IsCurrent, location?.ExplorerPath);
    }

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
        var (metadata, vocabulary) = await _metadata.GetAsync(Item.EntryId);
        var open = Fields.Where(f => f.ShowEvidence).Select(f => f.Field).ToHashSet();
        // A book owned elsewhere says first where it is owned.
        var fields = IsElsewhere ? metadata.Fields.OrderBy(f => f.Field != MetadataFields.AlsoOwn) : metadata.Fields;
        Fields = [.. fields.Select(f => new MetadataFieldViewModel(f, vocabulary, this, PrimaryFields.Contains(f.Field) || IsElsewhere && f.Field == MetadataFields.AlsoOwn)
        {
            ShowEvidence = open.Contains(f.Field),
        })];
        if (changed) MetadataChanged?.Invoke(this, EventArgs.Empty);
    }

    async Task<string?> IMetadataEditor.SaveAsync(MetadataField field, string typed)
    {
        var problem = await Task.Run(() => _metadata.SetAsync(Item.EntryId, field, typed));
        if (problem is not null) return problem.Message;
        await ReloadMetadataAsync(changed: true);
        return null;
    }

    async Task IMetadataEditor.KeepAsync(MetadataField field)
    {
        await Task.Run(() => _metadata.ConfirmAsync(Item.EntryId, field));
        await ReloadMetadataAsync(changed: true);
    }

    async Task IMetadataEditor.RejectAsync(MetadataField field, string normalized)
    {
        await Task.Run(() => _metadata.RejectAsync(Item.EntryId, field, normalized));
        await ReloadMetadataAsync(changed: true);
    }

    async Task IMetadataEditor.UseAsync(MetadataField field, string value)
    {
        await Task.Run(() => _metadata.UseAsync(Item.EntryId, field, value));
        await ReloadMetadataAsync(changed: true);
    }

    async Task IMetadataEditor.ResetAsync(MetadataField field)
    {
        await Task.Run(() => _metadata.ResetAsync(Item.EntryId, field));
        await ReloadMetadataAsync(changed: true);
    }

    public LibraryItemViewModel Item { get; }

    public string Title => Item.Title;

    /// <summary>The collections the book was added to (slice 3 plan, choice 10), as chips with ×.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCollections))]
    public partial IReadOnlyList<CollectionChip> Collections { get; private set; } = [];

    public bool HasCollections => Collections.Count > 0;

    public void ShowCollections(IReadOnlyList<CollectionChip> collections) => Collections = collections;

    public string Eyebrow => Item.IsPack ? "IMAGE PACK" : Item.IsElsewhere ? "OWNED ELSEWHERE" : Item.Entry.Format == SourceFormats.Pdf ? "PDF" : "IMAGE";

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

    /// <summary>The Notes tab (slice 3f): the book's own note and its page notes. Set by the page once the details load.</summary>
    [ObservableProperty]
    public partial EntryNotesViewModel? Notes { get; set; }

    /// <summary>The Notes tab is shown instead of the details.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDetailsTab))]
    public partial bool IsNotesTab { get; set; }

    public bool IsDetailsTab { get => !IsNotesTab; set { if (value) IsNotesTab = false; } }

    public static async Task<InspectorViewModel> LoadAsync(LibraryItemViewModel item, LibraryQueries queries, LibraryStore library, MetadataService metadata,
        IndexingService indexing, CopiesService copies, PackService packs, ElsewhereService elsewhere, CoverImages covers)
    {
        var details = await Task.Run(() => queries.GetDetailsAsync(item.EntryId));
        IReadOnlyList<InspectorLocation> locations;
        IReadOnlyList<CopyDetails> copyList = [];
        IReadOnlyList<PackImageViewModel> images = [];
        if (item.IsElsewhere) locations = [];
        else if (item.IsPack)
        {
            // A pack is where its folder or ZIP is; its images are listed below, each with its own file.
            var place = await Task.Run(() => packs.GetPlaceAsync(item.EntryId));
            locations = place is null ? [] : [new InspectorLocation(place.FullPath, place.IsArchive ? "The pack's ZIP" : "The pack's folder")];
            images = [.. (await Task.Run(() => queries.GetPackImagesAsync(item.EntryId))).Select(i => new PackImageViewModel(i, covers))];
        }
        else
        {
            locations = [.. (await library.GetLocationsAsync(item.DocumentId))
                .Select((l, i) => new InspectorLocation(l.FullPath, LocationState(l), ShowsReprocess: i == 0, l.ExplorerPath,
                    IsMissing: l.State == FileLocationState.Missing && l.RootAvailability == SourceRootAvailability.Online))];
            copyList = await Task.Run(() => copies.GetAsync(item.EntryId));
        }
        var inspector = new InspectorViewModel(item, details, locations, copyList, images, metadata, queries, indexing, copies, packs, elsewhere);
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
                var details = await Task.Run(() => _queries.GetDetailsAsync(Item.EntryId));
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
        // A pack's processing is its images', each read like any image.
        IReadOnlyList<InspectorStage> stages = details is null || entry.IsPack ? [] : [.. details.Stages.Select(s => new InspectorStage(StageName(s.Stage), StatusText(s)))];
        if (!stages.SequenceEqual(Stages)) Stages = stages;
        // AI cataloguing isn't part of Reprocess and can wait for hours (AI off, a model server down), so only the
        // stages that read the file count.
        IsReprocessing = !entry.IsPack && details?.Stages.Any(s => Pipeline.FileStages.Contains(s.Stage) && s.Status is StageStatus.Pending or StageStatus.Running) == true;
        PageCount = entry.Format == SourceFormats.Pdf ? entry.PageCount ?? 0 : 0;
    }

    /// <summary>
    /// Opens File Explorer with the file, or the ZIP it is in, selected; for a missing file, the folder it was last in,
    /// or the nearest one above it still there. Explorer only shows it; nothing is changed.
    /// </summary>
    [RelayCommand]
    static void ShowInExplorer(InspectorLocation location)
    {
        var path = location.ExplorerPath ?? location.Path;
        if (!location.IsMissing)
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });
            return;
        }
        var folder = System.IO.Path.GetDirectoryName(path);
        while (folder is not null && !System.IO.Directory.Exists(folder)) folder = System.IO.Path.GetDirectoryName(folder);
        if (folder is not null) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = false });
    }

    static List<InspectorFact> BuildFacts(LibraryEntry entry, DocumentDetails? details)
    {
        if (entry.IsElsewhere)
            return [new("File", "No file in your library"), new("Added", entry.AddedUtc.ToLocalTime().ToString("d MMMM yyyy", CultureInfo.CurrentCulture))];
        var facts = new List<InspectorFact>
        {
            new("Format", details is { WidthPx: { } w, HeightPx: { } h } && !entry.IsPack
                ? $"{LibraryItemViewModel.Describe(entry)} · {w.ToString("N0", CultureInfo.CurrentCulture)} × {h.ToString("N0", CultureInfo.CurrentCulture)} px"
                : LibraryItemViewModel.Describe(entry)),
            new("Added", entry.AddedUtc.ToLocalTime().ToString("d MMMM yyyy", CultureInfo.CurrentCulture)),
        };
        if (!string.IsNullOrEmpty(entry.FolderHint)) facts.Add(new("Folders", entry.FolderHint));
        if (details is null) return facts;

        facts.Add(new("Search", SearchText(entry, details)));
        // From how its stages stopped, not the file's encryption flag: that is set on any PDF with a security handler,
        // most of which open without a password (only restricting copying or printing), and never on a locked one,
        // which can't be opened to read it (slice 4i plan, choice 3).
        switch (entry.TextAccess)
        {
            case TextAccess.Locked:
                facts.Add(new("Password", "Needs a password to open. Open it to enter it; its text becomes searchable once it's unlocked."));
                break;
            case TextAccess.Protected:
                facts.Add(new("Protection", "A scheme Bibliotaph can't open, such as DRM. Its details stay editable, and Open in another app uses your default PDF app."));
                break;
        }
        // A file that forbids copying is still read for search; only copying text out stays off (slice 4i plan, choice 5).
        if (entry.Format == SourceFormats.Pdf && entry.TextAccess is TextAccess.Readable or TextAccess.Forgotten)
            facts.Add(new("Copying text", details.CanCopy ? "Allowed"
                : "Not allowed by the file. Search still finds its pages; copying text out of the reader stays off, as the file asks."));
        // The PDF's own information is often wrong ("Microsoft Word - final2.doc"), so it is labelled as the file's.
        if (!string.IsNullOrWhiteSpace(details.MetaTitle)) facts.Add(new("Title in the file", details.MetaTitle));
        if (!string.IsNullOrWhiteSpace(details.MetaAuthor)) facts.Add(new("Author in the file", details.MetaAuthor));
        if (!string.IsNullOrWhiteSpace(details.MetaSubject)) facts.Add(new("Subject in the file", details.MetaSubject));
        return facts;
    }

    static string SearchText(LibraryEntry entry, DocumentDetails details)
    {
        if (entry.IsPack || SourceFormats.IsImage(entry.Format)) return "Found by its name";
        switch (entry.TextAccess)
        {
            case TextAccess.Locked: return "Locked: open it to unlock, and its text becomes searchable";
            case TextAccess.Protected: return "Can't be read here, so it's found by its name and details only";
            case TextAccess.Forgotten: return "Its text was forgotten. Read it again, from its card's menu, to search it";
        }
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
        (FileLocationState.OnlineOnly, _) when location.ArchivePath is not null => "Inside an online-only ZIP",
        (FileLocationState.OnlineOnly, _) => "Online-only",
        _ when location.ArchivePath is not null => "Inside a ZIP",
        _ => "",
    };

    static string StageName(Stage stage) => stage switch
    {
        Stage.Probe => "Opening",
        Stage.Text => "Reading text",
        Stage.Covers => "Cover",
        Stage.RuleHints => "Hints from names",
        Stage.Ocr => "Reading scanned pages",
        Stage.Classify => "Cataloguing with AI",
        Stage.Match => "Looking for other copies",
        _ => stage.ToString(),
    };

    static string StatusText(StageState stage) => stage.Status switch
    {
        StageStatus.Complete => "Done",
        StageStatus.Partial => stage.Reason is null ? "Partly done" : $"Partly done: {stage.Reason}",
        StageStatus.Skipped => stage.Stage == Stage.Classify && stage.Reason is { } why ? why : "Not needed",
        StageStatus.Pending => "Waiting",
        StageStatus.Running => "Working on it…",
        _ => stage.Reason ?? stage.Status.ToString(),
    };
}

/// <summary>A collection the book in the details was added to: its chip opens it, and its × takes the book out.</summary>
public sealed record CollectionChip(long Id, string Name, string Path);
