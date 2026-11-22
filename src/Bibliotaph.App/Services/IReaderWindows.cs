using Bibliotaph.App.ViewModels;
using Bibliotaph.Pdf.Host;

namespace Bibliotaph.App.Services;

/// <summary>What pages and the reader ask of the reader windows: the main window's reader, pop-outs, and readers made for a page to hold.</summary>
public interface IReaderWindows
{
    /// <summary>A reader for <paramref name="request"/>, not yet shown anywhere.</summary>
    ViewerViewModel Create(ViewerRequest? request, bool poppedOut = false, bool inBinder = false);

    /// <summary>Opens it in the main window's reader, as clicking a book does.</summary>
    void OpenInMainWindow(ViewerRequest request);

    Task OpenInNewWindowAsync(ViewerRequest request);

    /// <summary>
    /// Shows it in <paramref name="reader"/>'s pop-out while that is open, else in a new one, and returns the reader
    /// showing it, so a page that opens book after book keeps one window.
    /// </summary>
    Task<ViewerViewModel> ShowInWindowAsync(ViewerViewModel? reader, ViewerRequest request);

    Task PopOutAsync(ViewerViewModel reader);

    void ReturnToMainWindow(ViewerViewModel reader);

    /// <summary>Whether a new reader shows the thumbnail strip: as the last reader left it.</summary>
    bool ShowsStrip { get; set; }

    /// <summary>The worker the main window's reader renders with; a pop-out leases its own.</summary>
    WorkerClient MainWorker { get; }
}
