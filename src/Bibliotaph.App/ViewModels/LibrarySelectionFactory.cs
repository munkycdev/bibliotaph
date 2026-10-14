using Bibliotaph.App.Services;
using Bibliotaph.Catalog;
using Bibliotaph.Processing;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.App.ViewModels;

/// <summary>
/// Makes a Library page's Select mode, so the page needs only to say which books it shows. Select mode logs as the
/// Library, the page it belongs to.
/// </summary>
public sealed class LibrarySelectionFactory(MetadataService metadata, CopiesService copies, PackService packs, LibraryStore library, PartsService parts,
    ExportService export, IFilePicker files, ILogger<LibraryViewModel> log)
{
    /// <summary>Select mode for <paramref name="page"/>, whose books it ticks.</summary>
    public LibrarySelectionViewModel Create(ILibrarySelectionPage page) =>
        new(page, metadata, copies, packs, library, parts, export, files, log);
}
