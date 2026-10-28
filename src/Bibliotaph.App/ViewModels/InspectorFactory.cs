using Bibliotaph.App.Services;
using Bibliotaph.Catalog;
using Bibliotaph.Index;
using Bibliotaph.Processing;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.App.ViewModels;

/// <summary>
/// Opens a book's details panel, with its Notes tab, so the page that shows it needs only the book. The details log as
/// the Library, the page they open on.
/// </summary>
public sealed class InspectorFactory(LibraryQueries queries, LibraryStore library, MetadataService metadata, IJobControl indexing, IStageCatalog stages,
    CopiesService copies, PackService packs, ElsewhereService elsewhere, CoverImages covers, PartsService parts, CorrectionService corrections, ChosenCovers chosenCovers,
    NotesService notes, IFilePicker files, IUserPrompts prompts, IDesktopShell shell, WebShelfCache shelf, TimeProvider time, ILogger<LibraryViewModel> log)
{
    /// <summary>The book's details, read from the library, with its own note and page notes loaded.</summary>
    public async Task<InspectorViewModel> LoadAsync(LibraryItemViewModel item)
    {
        var inspector = await InspectorViewModel.LoadAsync(item, queries, library, metadata, indexing, stages, copies, packs, elsewhere, covers,
            parts, files, prompts, shell, log, corrections, chosenCovers, shelf);
        inspector.Notes = new EntryNotesViewModel(item.EntryId, notes, time, log);
        await inspector.Notes.LoadAsync();
        return inspector;
    }
}
