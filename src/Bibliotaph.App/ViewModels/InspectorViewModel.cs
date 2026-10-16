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
/// instead: the folder it was in, or the nearest one above it that is still there.
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
    readonly IJobControl _indexing;
    readonly IStageCatalog _stages;
    readonly CopiesService _copies;
    readonly PackService _packs;
    readonly ElsewhereService _elsewhere;
    readonly CorrectionService? _corrections;
    readonly ChosenCovers? _chosen;
    readonly IFilePicker _files;
    readonly IUserPrompts _prompts;
    readonly IDesktopShell _shell;
    /// <summary>The metadata as last shown, so a correction can be compared with what the field held before it.</summary>
    EffectiveMetadata _shown = EffectiveMetadata.Empty;
    /// <summary>The user changed or kept a value since Cataloged wrong… opened the editor.</summary>
    bool _decided;
    bool _refreshing;
    bool _refreshAgain;

    InspectorViewModel(LibraryItemViewModel item, DocumentDetails? details, IReadOnlyList<InspectorLocation> locations, IReadOnlyList<CopyDetails> copies,
        IReadOnlyList<PackImageViewModel> images, MetadataService metadata, LibraryQueries queries, IJobControl indexing, IStageCatalog stages,
        CopiesService copiesService, PackService packs, ElsewhereService elsewhere, IFilePicker files, IUserPrompts prompts, IDesktopShell shell,
        CorrectionService? corrections, ChosenCovers? chosen, Microsoft.Extensions.Logging.ILogger log)
    {
        PartUndo = new UndoSlot(log);
        _files = files;
        _prompts = prompts;
        _shell = shell;
        _elsewhere = elsewhere;
        _chosen = chosen;
        _corrections = corrections;
        Item = item;
        _metadata = metadata;
        _queries = queries;
        _indexing = indexing;
        _stages = stages;
        _copies = copiesService;
        _packs = packs;
        Locations = locations;
        Copies = copies.Count < 2 ? [] : [.. copies.Select(ToCopy)];
        PackImages = images;
        ShowProcessing(details?.Entry ?? item.Entry, details);
    }

    /// <summary>A pack's files, images and PDFs, for its grid; empty for anything else.</summary>
    public IReadOnlyList<PackImageViewModel> PackImages { get; }

    public bool IsPack => Item.IsPack;

    /// <summary>A pack with PDFs among its images: it speaks of files, not images.</summary>
    bool HasPdfs => Item.HasPdfMembers || PackImages.Any(i => i.IsPdf);

    public string PackImagesHeading => $"{(HasPdfs ? "FILES" : "IMAGES")} ({PackImages.Count.ToString("N0", CultureInfo.CurrentCulture)})";

    /// <summary>The grid's name for screen readers.</summary>
    public string PackImagesName => HasPdfs ? "Files in the pack" : "Images in the pack";

    public string SplitPackLabel => HasPdfs ? "Split into separate files" : "Split into separate images";

    public string SplitPackTip => HasPdfs
        ? "Give every file its own card again. Bibliotaph won't pack this folder again."
        : "Give every image its own card again. Bibliotaph won't pack this folder again.";

    /// <summary>Raised after "Split into separate images" (or files), with the files that got their cards back.</summary>
    public event EventHandler<IReadOnlyList<EntryId>>? PackSplit;

    /// <summary>
    /// "Split into separate images", or "files" when PDFs are in the pack: every file gets its card back, and the
    /// folder isn't packed again.
    /// </summary>
    [RelayCommand]
    async Task SplitPack()
    {
        if (await Task.Run(() => _packs.SplitAsync(Item.EntryId)) is { } files) PackSplit?.Invoke(this, files);
    }

    /// <summary>One of the pack's files is its cover: "Use the mosaic" shows the mosaic again.</summary>
    public bool HasPackCover => PackImages.Any(i => i.IsCover);

    /// <summary>"Use as the pack's cover", from a file's menu in the grid: the card shows that file's cover in place of the mosaic.</summary>
    [RelayCommand]
    Task UsePackCover(PackImageViewModel image) => ShowPackCoverAsync(image);

    /// <summary>"Use the mosaic": the card shows the pack's first four files again.</summary>
    [RelayCommand]
    Task UseMosaic() => ShowPackCoverAsync(null);

    async Task ShowPackCoverAsync(PackImageViewModel? cover)
    {
        if (!await Task.Run(() => _packs.UseCoverAsync(Item.EntryId, cover?.Image.MemberEntryId))) return;
        foreach (var image in PackImages) image.IsCover = image == cover;
        OnPropertyChanged(nameof(HasPackCover));
        MetadataChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// A book owned elsewhere: no file, so no Open, locations or processing, and Remove from library.
    /// </summary>
    public bool IsElsewhere => Item.IsElsewhere;

    public bool CanOpen => !IsElsewhere;

    public string FactsHeading => IsElsewhere ? "IN YOUR LIBRARY" : "ABOUT THE FILE";

    /// <summary>Raised after "Remove from library", with what Undo needs.</summary>
    public event EventHandler<RemovedEntry>? Removed;

    /// <summary>
    /// "Remove from library", for a book owned elsewhere only: a file's card goes when its file does.
    /// </summary>
    [RelayCommand]
    async Task RemoveFromLibrary()
    {
        if (await Task.Run(() => _elsewhere.RemoveAsync(Item.EntryId)) is { } removed) Removed?.Invoke(this, removed);
    }

    /// <summary>A book owned elsewhere can have a picture of the user's own as its cover.</summary>
    public bool CanChooseCover => IsElsewhere && _chosen is not null;

    /// <summary>The book shows a cover the user chose, which Remove the cover takes away.</summary>
    [ObservableProperty]
    public partial bool HasChosenCover { get; private set; }

    /// <summary>Why the picture picked can't be the cover.</summary>
    [ObservableProperty]
    public partial string? CoverProblem { get; private set; }

    /// <summary>"Choose a cover image…": a picture from anywhere, shrunk to cover size and kept by Bibliotaph.</summary>
    [RelayCommand]
    async Task ChooseCover()
    {
        if (_chosen is null) return;
        if (_files.OpenFile($"Choose a cover for {Item.Title}", ChosenCovers.FileFilter) is not { } path) return;
        CoverProblem = await Task.Run(() => _chosen.ChooseAsync(Item.EntryId, path));
        if (CoverProblem is not null) return;
        HasChosenCover = true;
        MetadataChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    async Task RemoveCover()
    {
        if (_chosen is null) return;
        await Task.Run(() => _chosen.RemoveAsync(Item.EntryId));
        CoverProblem = null;
        HasChosenCover = false;
        MetadataChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Raised after the user changed this document's metadata, so the library can show it.</summary>
    public event EventHandler? MetadataChanged;

    /// <summary>
    /// Raised after Make current or Not the same book, with the card to show next: this one, or the copy's own card
    /// once it has been split off.
    /// </summary>
    public event EventHandler<EntryId>? CopiesChanged;

    /// <summary>The book's files when it has more than one: which opens, the others, and earlier versions.</summary>
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
    void ShowCopyInExplorer(InspectorCopy copy)
    {
        if ((copy.ExplorerPath ?? copy.Path) is { } path) _shell.ShowInFolder(path);
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
        if (copy.Joined) parts.Add("joined from a card of its own");
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

    /// <summary>
    /// An adventure, marked in a file or typed as one: who it is written for matters as much as its levels, so Players
    /// shows even before anyone has said.
    /// </summary>
    bool IsAdventure(EffectiveMetadata metadata) =>
        Item.IsPart || metadata[MetadataFields.Types].Values.Any(v => v.Value == PartStore.AdventureType);

    async Task ReloadMetadataAsync(bool changed)
    {
        var (metadata, vocabulary) = await _metadata.GetAsync(Item.EntryId);
        _shown = metadata;
        var open = Fields.Where(f => f.ShowEvidence).Select(f => f.Field).ToHashSet();
        // A book owned elsewhere says first where it is owned.
        var fields = IsElsewhere ? metadata.Fields.OrderBy(f => f.Field != MetadataFields.AlsoOwn) : metadata.Fields;
        var adventure = IsAdventure(metadata);
        Fields = [.. fields.Select(f => new MetadataFieldViewModel(f, vocabulary, this,
            PrimaryFields.Contains(f.Field) || IsElsewhere && f.Field == MetadataFields.AlsoOwn || adventure && f.Field == MetadataFields.Players)
        {
            ShowEvidence = open.Contains(f.Field),
            MarksGuesses = IsCorrecting,
        })];
        if (changed) MetadataChanged?.Invoke(this, EventArgs.Empty);
    }

    async Task<string?> IMetadataEditor.SaveAsync(MetadataField field, string typed)
    {
        var before = _shown[field];
        var problem = await Task.Run(() => _metadata.SetAsync(Item.EntryId, field, typed));
        if (problem is not null) return problem.Message;
        await ReloadMetadataAsync(changed: true);
        await CorrectedAsync(before);
        return null;
    }

    async Task IMetadataEditor.KeepAsync(MetadataField field)
    {
        await Task.Run(() => _metadata.ConfirmAsync(Item.EntryId, field));
        await ReloadMetadataAsync(changed: true);
        _decided = true;
    }

    async Task IMetadataEditor.RejectAsync(MetadataField field, string normalized)
    {
        var before = _shown[field];
        await Task.Run(() => _metadata.RejectAsync(Item.EntryId, field, normalized));
        await ReloadMetadataAsync(changed: true);
        await CorrectedAsync(before);
    }

    async Task IMetadataEditor.UseAsync(MetadataField field, string value)
    {
        var before = _shown[field];
        await Task.Run(() => _metadata.UseAsync(Item.EntryId, field, value));
        await ReloadMetadataAsync(changed: true);
        await CorrectedAsync(before);
    }

    async Task IMetadataEditor.ResetAsync(MetadataField field)
    {
        await Task.Run(() => _metadata.ResetAsync(Item.EntryId, field));
        await ReloadMetadataAsync(changed: true);
        _decided = true;
    }

    /// <summary>
    /// After a value was typed, chosen or rejected: the books from the same folder that showed what this one did are
    /// offered the same correction (<see cref="LookAlikes"/>). An offer stays until it is answered or a newer one replaces it.
    /// </summary>
    async Task CorrectedAsync(EffectiveField before)
    {
        _decided = true;
        if (_corrections is null) return;
        var after = _shown[before.Field];
        if (await Task.Run(() => _corrections.FindLookAlikesAsync(Item.EntryId, before, after)) is { } offer) LookAlikeOffer = offer;
    }

    // Easier corrections.

    /// <summary>
    /// Cataloged wrong…: the editor, with every field showing and the guesses marked and their evidence open, until Done
    /// or the details close. Left without a change or a kept value, the book gets a Needs review card that opens it again.
    /// </summary>
    [ObservableProperty]
    public partial bool IsCorrecting { get; private set; }

    /// <summary>Opens the editor for Cataloged wrong…, from the card's menu or the button in the details.</summary>
    [RelayCommand]
    public void StartCorrecting()
    {
        _decided = false;
        IsCorrecting = true;
        IsNotesTab = false;
        ShowAllFields = true;
        foreach (var field in Fields)
        {
            field.MarksGuesses = true;
            if (field.IsKnown && field.IsSuggested) field.ShowEvidence = true;
        }
    }

    /// <summary>Done, on the Cataloged wrong… banner.</summary>
    [RelayCommand]
    Task FinishCorrecting() => EndCorrectingAsync();

    /// <summary>
    /// Ends Cataloged wrong…, however the editor closed: with a correction made the book's card, if any, goes; with none,
    /// the book gets one. Raises <see cref="CorrectionEnded"/> with whether a card is left.
    /// </summary>
    public async Task EndCorrectingAsync()
    {
        if (!IsCorrecting) return;
        IsCorrecting = false;
        foreach (var field in Fields) field.MarksGuesses = false;
        if (_corrections is null) return;
        var unfinished = !_decided;
        if (unfinished) await Task.Run(() => _corrections.LeaveUnfinishedAsync(Item.EntryId));
        else await Task.Run(() => _corrections.FinishAsync(Item.EntryId));
        CorrectionEnded?.Invoke(this, unfinished);
    }

    /// <summary>Raised when Cataloged wrong… ends: true when the book was left with a Needs review card.</summary>
    public event EventHandler<bool>? CorrectionEnded;

    /// <summary>"Change 14 other books from “Fey Court” too?", after a correction that books from the same folder could use.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLookAlikeOffer), nameof(LookAlikeQuestion), nameof(LookAlikeDetail))]
    public partial LookAlikeOffer? LookAlikeOffer { get; private set; }

    public bool HasLookAlikeOffer => LookAlikeOffer is not null;

    public string LookAlikeQuestion => LookAlikeOffer is { } offer
        ? $"Change {offer.Books.Count.ToString("N0", CultureInfo.CurrentCulture)} other {(offer.Books.Count == 1 ? "book" : "books")} from “{offer.FolderName}” too?"
        : "";

    public string LookAlikeDetail => LookAlikeOffer is not { } offer ? ""
        : (offer.Books.Count == 1 ? "It" : "They") + (offer.Before.Count == 0
            ? $" had no {offer.Field.Label.ToLower(CultureInfo.CurrentCulture)} either"
            : $" show the {offer.Field.Label.ToLower(CultureInfo.CurrentCulture)} this book did")
          + $", and you haven't set it on {(offer.Books.Count == 1 ? "it" : "them")}. Undo takes it all back.";

    /// <summary>Raised by Change them, with the offer for the Library to apply as a bulk edit.</summary>
    public event EventHandler<LookAlikeOffer>? LookAlikesAccepted;

    [RelayCommand]
    void AcceptLookAlikes()
    {
        if (LookAlikeOffer is not { } offer) return;
        LookAlikeOffer = null;
        LookAlikesAccepted?.Invoke(this, offer);
    }

    [RelayCommand]
    void DeclineLookAlikes() => LookAlikeOffer = null;

    /// <summary>A Get it from link: only an http or https address, and only once the user says yes.</summary>
    void IMetadataEditor.OpenLink(string url)
    {
        if (StoreLink.WebAddress(url) is not { } address) return;
        if (!WebLinkPrompt.ConfirmOpen(_prompts, address) || _shell.Open(address) is null) return;
        _prompts.Tell("Windows couldn't open that link.", "Open a web link", PromptKind.Warning);
    }

    public LibraryItemViewModel Item { get; }

    public string Title => Item.Title;

    /// <summary>The collections the book was added to, as chips with ×.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCollections))]
    public partial IReadOnlyList<CollectionChip> Collections { get; private set; } = [];

    public bool HasCollections => Collections.Count > 0;

    public void ShowCollections(IReadOnlyList<CollectionChip> collections) => Collections = collections;

    public string Eyebrow => Item.IsPack ? HasPdfs ? "PACK" : "IMAGE PACK" : Item.IsElsewhere ? "OWNED ELSEWHERE" : Item.IsPart ? "ADVENTURE IN A FILE"
        : Item.Entry.Format == SourceFormats.Pdf ? "PDF" : "IMAGE";

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

    /// <summary>The Notes tab: the book's own note and its page notes. Set once the details load, as they open.</summary>
    [ObservableProperty]
    public partial EntryNotesViewModel? Notes { get; set; }

    /// <summary>The Notes tab is shown instead of the details.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDetailsTab))]
    public partial bool IsNotesTab { get; set; }

    /// <summary>Whether the book is in the sidebar's Pinned group, kept current by the Library.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PinLabel), nameof(PinAction))]
    public partial bool IsPinned { get; set; }

    public string PinLabel => IsPinned ? "Unpin" : "Pin";

    /// <summary>The pin button's tooltip and name.</summary>
    public string PinAction => IsPinned ? "Unpin from the sidebar and Home" : "Pin to the sidebar and Home";

    public bool IsDetailsTab { get => !IsNotesTab; set { if (value) IsNotesTab = false; } }

    public static async Task<InspectorViewModel> LoadAsync(LibraryItemViewModel item, LibraryQueries queries, LibraryStore library, MetadataService metadata,
        IJobControl indexing, IStageCatalog stages, CopiesService copies, PackService packs, ElsewhereService elsewhere, CoverImages covers,
        PartsService parts, IFilePicker files, IUserPrompts prompts, IDesktopShell shell, Microsoft.Extensions.Logging.ILogger log,
        CorrectionService? corrections = null, ChosenCovers? chosen = null)
    {
        var details = await Task.Run(() => queries.GetDetailsAsync(item.EntryId));
        IReadOnlyList<InspectorLocation> locations;
        IReadOnlyList<CopyDetails> copyList = [];
        IReadOnlyList<PackImageViewModel> images = [];
        if (item.IsElsewhere) locations = [];
        else if (item.IsPack)
        {
            // A pack is where its folder or ZIP is; its files are listed below, each with its own file. Files added by
            // hand, or a pack made by hand, name the other folders they are in.
            var place = await Task.Run(() => packs.GetPlaceAsync(item.EntryId));
            var folders = await Task.Run(() => packs.GetFoldersAsync(item.EntryId));
            images = [.. (await Task.Run(() => queries.GetPackImagesAsync(item.EntryId)))
                .Select(i => new PackImageViewModel(i, covers, isCover: i.MemberEntryId == item.Entry.CoverMember))];
            var noun = item.HasPdfMembers || images.Any(i => i.IsPdf) ? "files" : "images";
            locations = [.. place is null ? [] : new[] { new InspectorLocation(place.FullPath, place.IsArchive ? "The pack's ZIP" : "The pack's folder") },
                .. folders.Where(f => place is null || !string.Equals(f.FullPath, place.FullPath, StringComparison.OrdinalIgnoreCase))
                    .Select(f => new InspectorLocation(f.FullPath, $"{f.Files.ToString("N0", CultureInfo.CurrentCulture)} of its {noun} {(f.IsArchive ? "are in this ZIP" : "are here")}"))];
        }
        else
        {
            locations = [.. (await library.GetLocationsAsync(item.DocumentId))
                .Select((l, i) => new InspectorLocation(l.FullPath, LocationState(l), ShowsReprocess: i == 0, l.ExplorerPath,
                    IsMissing: l.State == FileLocationState.Missing && l.RootAvailability == SourceRootAvailability.Online))];
            // An adventure inside a file is in the file's copies; its file's details list them.
            if (!item.IsPart) copyList = await Task.Run(() => copies.GetAsync(item.EntryId));
        }
        var inspector = new InspectorViewModel(item, details, locations, copyList, images, metadata, queries, indexing, stages, copies, packs, elsewhere,
            files, prompts, shell, corrections, chosen, log);
        if (item.IsElsewhere && chosen is not null) inspector.HasChosenCover = await Task.Run(() => chosen.HasChosenAsync(item.EntryId));
        await inspector.ReloadMetadataAsync(changed: false);
        await inspector.LoadPartsAsync(parts);
        await inspector.LoadChaptersAsync(log);
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
        if (PageCount == 0 || !Services.ReprocessPrompt.ConfirmOcrEveryPage(_prompts, Title, PageCount)) return;
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
        IReadOnlyList<InspectorStage> stages = details is null || entry.IsPack
            ? []
            : [.. details.Stages.Select(s => new InspectorStage(_stages.Find(s.Stage)?.Names.Label ?? s.Stage.ToString(), StatusText(s)))];
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
    void ShowInExplorer(InspectorLocation location)
    {
        var path = location.ExplorerPath ?? location.Path;
        if (!location.IsMissing)
        {
            _shell.ShowInFolder(path);
            return;
        }
        var folder = System.IO.Path.GetDirectoryName(path);
        while (folder is not null && !System.IO.Directory.Exists(folder)) folder = System.IO.Path.GetDirectoryName(folder);
        if (folder is not null) _shell.OpenFolder(folder);
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
        // A pack's card shows its first file: what that file says of itself isn't the pack's.
        if (entry.IsPack)
        {
            facts.Add(new("Search", entry.PdfMembers > 0 ? "Found by its files' names, and by the text of its PDFs" : "Found by its name"));
            return facts;
        }

        facts.Add(new("Search", SearchText(entry, details)));
        // From how its stages stopped, not the file's encryption flag: that is set on any PDF with a security handler,
        // most of which open without a password (only restricting copying or printing), and never on a locked one,
        // which can't be opened to read it.
        switch (entry.TextAccess)
        {
            case TextAccess.Locked:
                facts.Add(new("Password", "Needs a password to open. Open it to enter it; its text becomes searchable once it's unlocked."));
                break;
            case TextAccess.Protected:
                facts.Add(new("Protection", "A scheme Bibliotaph can't open, such as DRM. Its details stay editable, and Open in another app uses your default PDF app."));
                break;
        }
        // A file that forbids copying is still read for search; only copying text out stays off.
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

    static string StatusText(StageState stage) => stage.Status switch
    {
        StageStatus.Complete => "Done",
        StageStatus.Partial => stage.Reason is null ? "Partly done" : $"Partly done: {stage.Reason}",
        StageStatus.Skipped => stage.Stage is Stage.Classify or Stage.ChapterScan && stage.Reason is { } why ? why : "Not needed",
        // A whole-book scan waits a long time, and says how far it has got or what it waits for.
        StageStatus.Pending when stage.Stage == Stage.ChapterScan && stage.Reason is { } waiting => waiting,
        StageStatus.Pending => "Waiting",
        StageStatus.Running => "Working on it…",
        _ => stage.Reason ?? stage.Status.ToString(),
    };
}

/// <summary>A collection the book in the details was added to: its chip opens it, and its × takes the book out.</summary>
public sealed record CollectionChip(long Id, string Name, string Path);
