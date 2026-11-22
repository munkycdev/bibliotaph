using AppKit;
using Bibliotaph.Mac.Binding;
using Bibliotaph.Mac.Views;
using Bibliotaph.Pdf.Host;
using Bibliotaph.Presentation.Services;
using Bibliotaph.Presentation.ViewModels;
using CoreGraphics;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.Mac.Platform;

/// <summary>
/// Where books open: in the main window in place of the Library, with Back to return, or in a window of their own. A
/// reader window holds its own viewer worker, so two books draw at once.
/// </summary>
public sealed class MacReaderWindows(ViewerServices viewer, INavigationService navigation, ILogger<MacReaderWindows> log) : IReaderWindows
{
    readonly List<ReaderWindow> _windows = [];
    ViewerLease? _mainLease;

    public bool ShowsStrip { get; set; }

    public WorkerClient MainWorker => (_mainLease ??= viewer.Workers.LeaseViewer()).Worker;

    public ViewerViewModel Create(ViewerRequest? request, bool poppedOut = false, bool inBinder = false) =>
        new(request, viewer, this, poppedOut) { InBinder = inBinder };

    public void OpenInMainWindow(ViewerRequest request)
    {
        navigation.Show(Create(request));
        BringMainForward();
    }

    public Task OpenInNewWindowAsync(ViewerRequest request) => HostAsync(Create(request, poppedOut: true));

    public async Task<ViewerViewModel> ShowInWindowAsync(ViewerViewModel? reader, ViewerRequest request)
    {
        if (reader is null || _windows.Find(w => ReferenceEquals(w.Model, reader)) is not { } window)
        {
            var created = Create(request, poppedOut: true);
            await HostAsync(created);
            return created;
        }
        window.Window!.MakeKeyAndOrderFront(null);
        try
        {
            await reader.ShowAsync(request);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            log.LogError(ex, "Showing another book in a reader window failed");
        }
        return reader;
    }

    public async Task PopOutAsync(ViewerViewModel reader)
    {
        if (reader.Here() is not { } here) return;
        if (navigation.CanGoBack) navigation.GoBack();
        await OpenInNewWindowAsync(here);
    }

    public void ReturnToMainWindow(ViewerViewModel reader)
    {
        if (reader.Here() is not { } here || _windows.Find(w => ReferenceEquals(w.Model, reader)) is not { } window) return;
        window.Close();
        OpenInMainWindow(here);
    }

    public async Task HostAsync(PageViewModel model)
    {
        var window = new ReaderWindow(model, this);
        _windows.Add(window);
        window.ShowWindow(null);
        try
        {
            await model.LoadAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            log.LogError(ex, "Opening a reader window failed");
        }
    }

    public void Close(PageViewModel model) => _windows.Find(w => ReferenceEquals(w.Model, model))?.Close();

    public bool Shows(Func<PageViewModel, bool> match) =>
        _windows.Exists(w => match(w.Model)) || (navigation.Current is { } current && match(current));

    public bool BringForward(Func<PageViewModel, bool> match)
    {
        if (_windows.Find(w => match(w.Model)) is not { } window) return false;
        window.Window!.MakeKeyAndOrderFront(null);
        return true;
    }

    public void BringMainForward() => Bindings.OnMain(() =>
        MainWindow?.MakeKeyAndOrderFront(null));

    /// <summary>The main window, which MainWindowController names here when it opens.</summary>
    public NSWindow? MainWindow { get; set; }

    internal void Closed(ReaderWindow window)
    {
        _windows.Remove(window);
        window.Model.CloseWindowAsync().FireAndForget();
    }
}

/// <summary>A reader in a window of its own, titled with its book.</summary>
sealed class ReaderWindow : NSWindowController
{
    readonly MacReaderWindows _owner;
    readonly Bindings _bindings = new();

    public ReaderWindow(PageViewModel model, MacReaderWindows owner)
        : base(new NSWindow(new CGRect(0, 0, 980, 1000),
            NSWindowStyle.Titled | NSWindowStyle.Closable | NSWindowStyle.Miniaturizable | NSWindowStyle.Resizable | NSWindowStyle.FullSizeContentView,
            NSBackingStore.Buffered, false))
    {
        Model = model;
        _owner = owner;
        var window = Window!;
        window.ReleaseWhenClosed(false);
        window.TitlebarAppearsTransparent = false;
        window.ContentViewController = model is ViewerViewModel reader ? new ReaderController(reader, null) : new NSViewController { View = new NSView() };
        window.SetContentSize(new CGSize(980, 1000));
        window.Center();
        window.FrameAutosaveName = "BibliotaphReader";
        _bindings.Property(model, nameof(PageViewModel.Title), () => window.Title = model.Title);
        window.WillClose += (_, _) =>
        {
            _bindings.Dispose();
            _owner.Closed(this);
        };
    }

    public PageViewModel Model { get; }
}
