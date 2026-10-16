using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Bibliotaph.App.ViewModels;

namespace Bibliotaph.App.Views;

/// <summary>
/// Connects the page surface, which is driven by calls rather than bindings, to the viewer's view model: it loads the
/// open PDF, shows the highlights, scrolls where the model asks and reports the page in view. It draws a book opened
/// ahead of time at the size the pages show at, and tells the thumbnail strip which thumbnails are in view. An
/// e-book's page surface is driven the same way.
/// </summary>
public partial class ViewerView
{
    ViewerViewModel? _model;
    PageStripViewModel? _strip;
    FindViewModel? _find;
    OpenPdf? _shown;
    OpenEbook? _shownEbook;
    ScrollViewer? _stripScroll;
    bool _drawQueued;

    public ViewerView()
    {
        InitializeComponent();
        PagesView.CurrentPageChanged += (_, _) => _model?.CurrentPageIndex = PagesView.CurrentPageIndex;
        PagesView.Notice += (_, text) => _model?.ShowNotice(text);
        PagesView.LinkClicked += (_, link) => _model?.FollowLink(link);
        EbookSurface.CurrentPageChanged += (_, page) =>
        {
            if (_model is { IsEbook: true } model) model.CurrentPageIndex = page;
        };
        EbookSurface.LinkFollowed += (_, from) => _model?.LinkFollowed(from);
        EbookSurface.WebLinkClicked += (_, address) => _model?.FollowLink(new Bibliotaph.Viewer.PageLink(default, -1, null, address));
        EbookSurface.ZoomRequested += (_, step) => _model?.StepEbookZoom(step);
        EbookSurface.Failed += (_, message) => _model?.ShowEbookProblem(message);
        EbookSurface.KeyPressed += (_, key) =>
        {
            if (_model is not { IsEbook: true } model) return;
            if (key == "find") FocusFind();
            else if (key == "next") model.Find.NextCommand.Execute(null);
            else if (key == "previous") model.Find.PreviousCommand.Execute(null);
        };
        DataContextChanged += (_, _) => Attach(DataContext as ViewerViewModel);
        Loaded += (_, _) => Attach(DataContext as ViewerViewModel);
        Unloaded += (_, _) => Attach(null);
        PreviewKeyDown += OnPreviewKeyDown;
        CommandBindings.Add(new CommandBinding(NavigationCommands.IncreaseZoom, (_, _) => StepZoom(up: true), CanZoom));
        CommandBindings.Add(new CommandBinding(NavigationCommands.DecreaseZoom, (_, _) => StepZoom(up: false), CanZoom));
    }

    void Attach(ViewerViewModel? model)
    {
        if (ReferenceEquals(_model, model)) return;
        if (_model is not null)
        {
            _model.PropertyChanged -= OnModelChanged;
            _model.GoToRequested -= OnGoToRequested;
            _model.DrawAhead = null;
            _model.EbookFind = null;
            _model.EbookTerms = null;
        }
        _strip?.PropertyChanged -= OnStripChanged;
        _find?.PropertyChanged -= OnFindChanged;
        _model = model;
        _strip = model?.Strip;
        _find = model?.Find;
        if (model is null)
        {
            ShowPdf(null);
            ShowEbook(null);
            return;
        }
        model.PropertyChanged += OnModelChanged;
        model.GoToRequested += OnGoToRequested;
        model.DrawAhead = (renderer, doc, page) => PagesView.PrerenderAsync(renderer, doc, page);
        model.EbookFind = EbookSurface.FindAsync;
        model.EbookTerms = EbookSurface.MarkTermsAsync;
        EbookSurface.Browser = model.EbookBrowser;
        _strip!.PropertyChanged += OnStripChanged;
        _find!.PropertyChanged += OnFindChanged;
        ShowPdf(model.Pdf);
        ShowEbook(model.Ebook);
        ShowHighlights();
        KeepCurrentThumbnailInView();
    }

    void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ViewerViewModel.Pdf):
                ShowPdf(_model?.Pdf);
                break;
            case nameof(ViewerViewModel.Ebook):
                ShowEbook(_model?.Ebook);
                break;
            case nameof(ViewerViewModel.Zoom) when _model is { IsEbook: true }:
                ZoomEbook();
                break;
            case nameof(ViewerViewModel.IsPdf) when _model?.IsPdf == true:
                // The pages take keyboard focus once they're shown, so Ctrl+A and the arrow keys work straight away.
                Dispatcher.BeginInvoke(DispatcherPriority.Input, () => PagesView.Focus());
                CommandManager.InvalidateRequerySuggested();
                break;
            case nameof(ViewerViewModel.AskVisible) when _model?.AskVisible == true:
                // Opening Ask is for typing a question.
                Dispatcher.BeginInvoke(DispatcherPriority.Input, () => AskBox.Focus());
                break;
            case nameof(ViewerViewModel.IsPdf) or nameof(ViewerViewModel.IsImage) or nameof(ViewerViewModel.IsEbook):
                // The zoom buttons and keys follow what's open.
                CommandManager.InvalidateRequerySuggested();
                break;
            case nameof(ViewerViewModel.StripVisible) when _model?.StripVisible == true:
                KeepCurrentThumbnailInView();
                break;
        }
    }

    void OnFindChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FindViewModel.Highlights)) ShowHighlights();
    }

    // ---------- The thumbnail strip ----------

    void OnStripChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PageStripViewModel.Current) or nameof(PageStripViewModel.Pages)) KeepCurrentThumbnailInView();
    }

    /// <summary>The page in view's thumbnail stays in sight as the reader scrolls the pages, once the strip is laid out.</summary>
    void KeepCurrentThumbnailInView()
    {
        if (_model is not { StripVisible: true }) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (_strip?.Current is { } current && _model is { StripVisible: true }) PageStrip.ScrollIntoView(current);
            QueueDraw();
        });
    }

    /// <summary>The strip scrolled or was laid out: the thumbnails now in view, and a few either side, are drawn.</summary>
    void PageStrip_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.OriginalSource is ScrollViewer scroll) _stripScroll = scroll;
        QueueDraw();
    }

    void QueueDraw()
    {
        if (_drawQueued) return;
        _drawQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _drawQueued = false;
            // The strip scrolls by whole thumbnails, so the offset and the viewport count thumbnails.
            if (_strip is null || _model is not { StripVisible: true } || (_stripScroll ??= FindChild<ScrollViewer>(PageStrip)) is not { } strip) return;
            var first = (int)strip.HorizontalOffset;
            _strip.Draw(first, first + Math.Max(0, (int)Math.Ceiling(strip.ViewportWidth) - 1));
        });
    }

    /// <summary>The strip runs sideways, so the mouse wheel scrolls it sideways.</summary>
    void PageStrip_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((_stripScroll ??= FindChild<ScrollViewer>(PageStrip)) is not { } strip) return;
        for (var i = 0; i < 2; i++)
        {
            if (e.Delta > 0) strip.LineLeft();
            else strip.LineRight();
        }
        e.Handled = true;
    }

    static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            if (FindChild<T>(child) is { } deeper) return deeper;
        }
        return null;
    }

    void ShowPdf(OpenPdf? pdf)
    {
        if (ReferenceEquals(_shown, pdf)) return;
        _shown = pdf;
        if (pdf is null) PagesView.Unload();
        else PagesView.Load(pdf.Renderer, pdf.Doc, pdf.Text, pdf.PageIndex, pdf.PdfTop, pdf.Seed, pdf.Numbering);
    }

    void ShowEbook(OpenEbook? ebook)
    {
        if (ReferenceEquals(_shownEbook, ebook)) return;
        _shownEbook = ebook;
        if (ebook is null) EbookSurface.Unload();
        else
        {
            ZoomEbook();
            EbookSurface.Show(ebook.Content, ebook.PageIndex);
        }
    }

    void ZoomEbook()
    {
        if (_model is not { } model) return;
        EbookSurface.SetZoom(model.Zoom > 0 ? model.Zoom : 1, fitPage: model.Zoom == Bibliotaph.Viewer.PdfPagesView.FitPage);
    }

    void ShowHighlights()
    {
        if (_find?.Highlights is not { } highlights) return;
        // An e-book's page keeps the matches it found; it is told which list to mark and which match stands out.
        if (_shownEbook is not null) EbookSurface.ShowMatches(highlights.Items.Count == 0 ? null : _find!.ShowsTerms ? "terms" : "find", highlights.Current);
        else PagesView.ShowHighlights(highlights.Items, highlights.Current);
    }

    void OnGoToRequested(object? sender, PageTarget target)
    {
        if (_model?.IsEbook == true || _shownEbook is not null) EbookSurface.GoTo(target.PageIndex);
        else PagesView.GoToPage(target.PageIndex, target.PdfTop);
    }

    void FocusFind()
    {
        FindBox.Focus();
        FindBox.SelectAll();
    }

    /// <summary>The page with selected text on it, on whichever surface shows the book.</summary>
    int? SelectionPage => _model?.IsEbook == true ? EbookSurface.SelectionPageIndex : PagesView.SelectionPageIndex;

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control && _model?.HasPages == true)
        {
            FocusFind();
            e.Handled = true;
        }
        // In a pack, the arrow keys step through its images, ahead of the image's scroll bars.
        else if (e.Key is Key.Left or Key.Right && Keyboard.Modifiers == ModifierKeys.None && _model is { IsImage: true, IsInPack: true }
            && e.OriginalSource is not System.Windows.Controls.Primitives.TextBoxBase)
        {
            var command = e.Key == Key.Left ? _model.PreviousImageCommand : _model.NextImageCommand;
            if (command.CanExecute(null)) command.Execute(null);
            e.Handled = true;
        }
    }

    // The step starts from the size on screen, which only the surface knows when the pages are fitted.
    void StepZoom(bool up)
    {
        if (_model?.IsPdf == true) PagesView.StepZoom(up);
        else if (_model?.IsImage == true) ImageSurface.StepZoom(up);
        else _model?.StepEbookZoom(up ? 1 : -1);
    }

    void CanZoom(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = _model is { IsPdf: true } or { IsImage: true } or { IsEbook: true };

    void PageBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => PageBox.SelectAll();

    /// <summary>The page with selected text on it as the Add page menu opened, before the click takes focus from the pages.</summary>
    int? _selectionPage;

    void AddPageMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button) return;
        _selectionPage = SelectionPage;
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    void AddPages_Click(object sender, RoutedEventArgs e) => _model?.AddPagesCommand.Execute(_selectionPage);

    /// <summary>Note: on the page with selected text, if there is a selection, else the page in view.</summary>
    void AddNote_Click(object sender, RoutedEventArgs e) => _model?.Notes.AddCommand.Execute(SelectionPage);

    /// <summary>A bookmark's Index as its own adventure: the menu's row is the bookmark.</summary>
    void IndexBookmark_Click(object sender, RoutedEventArgs e) => _model?.IndexBookmarkCommand.Execute((sender as FrameworkElement)?.DataContext as OutlineEntry);
}
