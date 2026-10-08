using System.Diagnostics;
using System.Globalization;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Index;
using CommunityToolkit.Mvvm.Input;

namespace Bibliotaph.App.ViewModels;

public sealed record InspectorFact(string Label, string Value);

/// <summary>Where a document's file is, and whether it can be read now.</summary>
public sealed record InspectorLocation(string Path, string State)
{
    public bool HasState => State.Length > 0;
}

public sealed record InspectorStage(string Name, string Status);

/// <summary>
/// The simple slice 1 inspector: what Bibliotaph knows about one document from its file (pages, capabilities,
/// where it is, how far indexing got). Metadata, suggestions and evidence arrive in slice 2.
/// </summary>
public sealed partial class InspectorViewModel
{
    InspectorViewModel(LibraryItemViewModel item, DocumentDetails? details, IReadOnlyList<DocumentLocation> locations)
    {
        Item = item;
        Facts = BuildFacts(item.Entry, details);
        Locations = [.. locations.Select(l => new InspectorLocation(l.FullPath, LocationState(l)))];
        Stages = details is null ? [] : [.. details.Stages.Select(s => new InspectorStage(StageName(s.Stage), StatusText(s)))];
    }

    public LibraryItemViewModel Item { get; }

    public string Title => Item.Title;

    public string Eyebrow => Item.Entry.Format == SourceFormats.Pdf ? "PDF" : "IMAGE";

    public IReadOnlyList<InspectorFact> Facts { get; }

    public IReadOnlyList<InspectorLocation> Locations { get; }

    public IReadOnlyList<InspectorStage> Stages { get; }

    public bool HasStages => Stages.Count > 0;

    public static async Task<InspectorViewModel> LoadAsync(LibraryItemViewModel item, LibraryQueries queries, LibraryStore library)
    {
        var details = await Task.Run(() => queries.GetDetailsAsync(item.DocumentId));
        var locations = await library.GetLocationsAsync(item.DocumentId);
        return new InspectorViewModel(item, details, locations);
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
        Stage.Ocr => "Reading scanned pages",
        _ => stage.ToString(),
    };

    static string StatusText(StageState stage) => stage.Status switch
    {
        StageStatus.Complete => "Done",
        StageStatus.Partial => stage.Reason is null ? "Partly done" : $"Partly done: {stage.Reason}",
        StageStatus.Skipped => "Not needed",
        StageStatus.Pending or StageStatus.Running => "Waiting",
        _ => stage.Reason ?? stage.Status.ToString(),
    };
}
