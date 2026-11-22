using Bibliotaph.Pdf.Host;
using Bibliotaph.Presentation.Services;
using Bibliotaph.Presentation.ViewModels;

namespace Bibliotaph.Mac.Platform;

/// <summary>
/// Where books open. Until the Mac has its own reader, a book opens in the Mac's default app for its file, so the
/// library is useful while the reader is built.
/// </summary>
public sealed class MacReaderWindows(ViewerServices viewer, OtherApps otherApps, IUserPrompts prompts) : IReaderWindows
{
    ViewerLease? _mainLease;

    public bool ShowsStrip { get; set; }

    public WorkerClient MainWorker => (_mainLease ??= viewer.Workers.LeaseViewer()).Worker;

    public ViewerViewModel Create(ViewerRequest? request, bool poppedOut = false, bool inBinder = false) =>
        new(request, viewer, this, poppedOut) { InBinder = inBinder };

    public void OpenInMainWindow(ViewerRequest request) => OpenElsewhereAsync(request).FireAndForget();

    public Task OpenInNewWindowAsync(ViewerRequest request) => OpenElsewhereAsync(request);

    public async Task<ViewerViewModel> ShowInWindowAsync(ViewerViewModel? reader, ViewerRequest request)
    {
        await OpenElsewhereAsync(request);
        return reader ?? Create(request);
    }

    public Task PopOutAsync(ViewerViewModel reader) => Task.CompletedTask;

    public void ReturnToMainWindow(ViewerViewModel reader)
    {
    }

    public Task HostAsync(PageViewModel model) => Task.CompletedTask;

    public void Close(PageViewModel model)
    {
    }

    public bool Shows(Func<PageViewModel, bool> match) => false;

    public bool BringForward(Func<PageViewModel, bool> match) => false;

    public void BringMainForward()
    {
    }

    async Task OpenElsewhereAsync(ViewerRequest request)
    {
        if (await otherApps.OpenAsync(request.DocumentId) is { } problem) prompts.Tell(problem, request.Title, PromptKind.Warning);
    }
}

/// <summary>The reader's own page drawing comes with the Mac reader; until then nothing asks for it.</summary>
public sealed class NoReaderDocuments : IReaderDocuments
{
    public IReaderDocument Create(WorkerClient worker) => throw new NotSupportedException("The Mac reader isn't built yet.");
}

/// <summary>The reader's page strip comes with the Mac reader.</summary>
public sealed class NoPageThumbnails : IPageThumbnails
{
    public object? TryGet(string contentHash, int pageIndex) => null;

    public Task<object?> LoadAsync(string contentHash, int pageIndex, double scale, IReaderDocument document, bool keepOnDisk, CancellationToken ct) =>
        Task.FromResult<object?>(null);

    public void Touch(string contentHash)
    {
    }
}
