using AppKit;
using Bibliotaph.Core.Reading;
using Bibliotaph.Mac.Binding;
using Bibliotaph.Mac.Platform;
using Bibliotaph.Presentation.Services;
using Bibliotaph.Presentation.ViewModels;
using CoreGraphics;

namespace Bibliotaph.Mac.Views;

/// <summary>
/// The reader for <see cref="ViewerViewModel"/>: a bar with the way back, the page box, zoom and find, over the pages,
/// a picture, or what went wrong. The view model opens the book, asks for its password and keeps the reader's place.
/// </summary>
public sealed class ReaderController(ViewerViewModel reader, INavigationService? navigation) : NSViewController
{
    readonly Bindings _bindings = new();
    PagesView _pages = null!;
    OutlineList _outline = null!;
    StripView _strip = null!;
    NotesPane _notesPane = null!;
    AskPane _askPane = null!;
    OpenPdf? _shownPdf;
    OpenEbook? _shownEbook;
    EbookSurface _ebook = null!;
    VideoSurface _video = null!;

    public override void LoadView()
    {
        var back = Button("chevron.backward", "Back", () => navigation?.GoBack());
        back.Hidden = navigation is null;
        var title = NSTextField.CreateLabel("");
        title.Font = NSFont.SystemFontOfSize(13, NSFontWeight.Semibold)!;
        title.LineBreakMode = NSLineBreakMode.TruncatingMiddle;
        title.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);

        var previous = Button("chevron.up", "Previous page", null);
        var next = Button("chevron.down", "Next page", null);
        var pageBox = new NSTextField { Alignment = NSTextAlignment.Right, PlaceholderString = "Page" };
        pageBox.WidthAnchor.ConstraintEqualTo(56).Active = true;
        var pageCount = NSTextField.CreateLabel("");
        pageCount.TextColor = NSColor.SecondaryLabel;
        var zoomOut = Button("minus.magnifyingglass", "Zoom out", () => StepZoom(-1));
        var zoomIn = Button("plus.magnifyingglass", "Zoom in", () => StepZoom(1));
        var fit = Button("arrow.left.and.right", "Fit width", null);
        // The whole page in view, as Preview's Zoom to Fit: the page being read, from its top.
        var fitPage = Button("arrow.up.and.down.and.arrow.left.and.right", "Fit the whole page", FitPage);
        var find = new NSSearchField { PlaceholderString = "Find in this book", SendsWholeSearchString = true };
        find.WidthAnchor.ConstraintEqualTo(220).Active = true;
        var findPrevious = Button("chevron.left", "Previous match", null);
        var findNext = Button("chevron.right", "Next match", null);
        var findStatus = NSTextField.CreateLabel("");
        findStatus.TextColor = NSColor.SecondaryLabel;
        findStatus.Font = NSFont.SystemFontOfSize(11)!;

        var outlineToggle = Button("sidebar.left", "Bookmarks", null);
        var stripToggle = Button("rectangle.bottomthird.inset.filled", "Page thumbnails", null);
        var notesToggle = Button("note.text", "Notes", null);
        var askToggle = Button("bubble.left.and.text.bubble.right", "Ask this book", null);
        _bindings.Command(askToggle, reader.ToggleAskCommand);
        var addToBinder = Button("book.closed", "Add this page to a binder", null);
        addToBinder.Activated += (_, _) =>
        {
            // The binders to add to, as they are when the menu opens, as the Windows reader's menu lists them.
            var menu = new NSMenu();
            foreach (var choice in reader.AddToBinderChoices)
                menu.AddItem(new NSMenuItem(choice.Label, (_, _) => reader.AddPageToSessionCommand.Execute(new SessionRequest(null, choice))));
            menu.PopUpMenu(null, new CoreGraphics.CGPoint(0, addToBinder.Bounds.Height + 4), addToBinder);
        };
        _bindings.Command(notesToggle, reader.ToggleNotesCommand);
        _bindings.Command(outlineToggle, reader.ToggleOutlineCommand);
        _bindings.Command(stripToggle, reader.ToggleStripCommand);
        var bar = NSStackView.FromViews([back, outlineToggle, title, new NSView(), addToBinder, askToggle, notesToggle, stripToggle, previous, next, pageBox, pageCount, Separator(), zoomOut, zoomIn, fit, fitPage, Separator(),
            findStatus, find, findPrevious, findNext]);
        bar.Orientation = NSUserInterfaceLayoutOrientation.Horizontal;
        bar.Spacing = 6;
        bar.EdgeInsets = new NSEdgeInsets(6, 12, 6, 12);

        _pages = new PagesView();
        var picture = new NSImageView { ImageScaling = NSImageScale.ProportionallyDown };
        var pictureScroll = new NSScrollView { DocumentView = picture, HasVerticalScroller = true, HasHorizontalScroller = true, AutohidesScrollers = true };

        var messageTitle = NSTextField.CreateWrappingLabel("");
        messageTitle.Font = NSFont.SystemFontOfSize(17, NSFontWeight.Semibold)!;
        messageTitle.Alignment = NSTextAlignment.Center;
        var messageBody = NSTextField.CreateWrappingLabel("");
        messageBody.TextColor = NSColor.SecondaryLabel;
        messageBody.Alignment = NSTextAlignment.Center;
        var action = NSButton.CreateButton("", () => Run(reader.EmptyCommand));
        var secondary = NSButton.CreateButton("", () => Run(reader.EmptySecondaryCommand));
        var spinner = new NSProgressIndicator { Style = NSProgressIndicatorStyle.Spinning };
        var message = NSStackView.FromViews([spinner, messageTitle, messageBody, action, secondary]);
        message.Orientation = NSUserInterfaceLayoutOrientation.Vertical;
        message.Spacing = 10;
        var notice = NSTextField.CreateWrappingLabel("");
        notice.TextColor = NSColor.SystemOrange;

        // The book's area: the pages, a picture or what's wrong, with its bookmarks beside it and its page strip under it.
        var content = new NSView();
        _ebook = new EbookSurface();
        _video = new VideoSurface();
        foreach (var view in new NSView[] { _pages, _ebook, _video, pictureScroll, message })
        {
            view.TranslatesAutoresizingMaskIntoConstraints = false;
            content.AddSubview(view);
        }
        _outline = new OutlineList(reader);
        _strip = new StripView(reader.Strip);
        _notesPane = new NotesPane(reader.Notes);
        _askPane = new AskPane(reader.Ask);
        var row = NSStackView.FromViews([_outline, content, _notesPane, _askPane]);
        row.Spacing = 0;
        row.Alignment = NSLayoutAttribute.Height;
        var column = NSStackView.FromViews([row, _strip]);
        column.Orientation = NSUserInterfaceLayoutOrientation.Vertical;
        column.Spacing = 0;
        column.Alignment = NSLayoutAttribute.Width;

        var root = new NSView();
        foreach (var view in new NSView[] { bar, column, notice })
        {
            view.TranslatesAutoresizingMaskIntoConstraints = false;
            root.AddSubview(view);
        }
        NSLayoutConstraint.ActivateConstraints(
        [
            bar.TopAnchor.ConstraintEqualTo(root.SafeAreaLayoutGuide.TopAnchor),
            bar.LeadingAnchor.ConstraintEqualTo(root.LeadingAnchor),
            bar.TrailingAnchor.ConstraintEqualTo(root.TrailingAnchor),
            notice.TopAnchor.ConstraintEqualTo(bar.BottomAnchor),
            notice.LeadingAnchor.ConstraintEqualTo(root.LeadingAnchor, 16),
            notice.TrailingAnchor.ConstraintLessThanOrEqualTo(root.TrailingAnchor, -16),
            column.TopAnchor.ConstraintEqualTo(notice.BottomAnchor, 2),
            column.LeadingAnchor.ConstraintEqualTo(root.LeadingAnchor),
            column.TrailingAnchor.ConstraintEqualTo(root.TrailingAnchor),
            column.BottomAnchor.ConstraintEqualTo(root.BottomAnchor),
            _outline.WidthAnchor.ConstraintEqualTo(250),
            _notesPane.WidthAnchor.ConstraintEqualTo(280),
            _askPane.WidthAnchor.ConstraintEqualTo(320),
            _strip.HeightAnchor.ConstraintEqualTo(150),
            _pages.TopAnchor.ConstraintEqualTo(content.TopAnchor),
            _pages.LeadingAnchor.ConstraintEqualTo(content.LeadingAnchor),
            _pages.TrailingAnchor.ConstraintEqualTo(content.TrailingAnchor),
            _pages.BottomAnchor.ConstraintEqualTo(content.BottomAnchor),
            _video.TopAnchor.ConstraintEqualTo(content.TopAnchor),
            _video.LeadingAnchor.ConstraintEqualTo(content.LeadingAnchor),
            _video.TrailingAnchor.ConstraintEqualTo(content.TrailingAnchor),
            _video.BottomAnchor.ConstraintEqualTo(content.BottomAnchor),
            _ebook.TopAnchor.ConstraintEqualTo(content.TopAnchor),
            _ebook.LeadingAnchor.ConstraintEqualTo(content.LeadingAnchor),
            _ebook.TrailingAnchor.ConstraintEqualTo(content.TrailingAnchor),
            _ebook.BottomAnchor.ConstraintEqualTo(content.BottomAnchor),
            pictureScroll.TopAnchor.ConstraintEqualTo(content.TopAnchor),
            pictureScroll.LeadingAnchor.ConstraintEqualTo(content.LeadingAnchor),
            pictureScroll.TrailingAnchor.ConstraintEqualTo(content.TrailingAnchor),
            pictureScroll.BottomAnchor.ConstraintEqualTo(content.BottomAnchor),
            message.CenterXAnchor.ConstraintEqualTo(content.CenterXAnchor),
            message.CenterYAnchor.ConstraintEqualTo(content.CenterYAnchor),
            message.WidthAnchor.ConstraintLessThanOrEqualTo(460),
        ]);
        View = root;

        _bindings.Property(reader, nameof(ViewerViewModel.Title), () => title.StringValue = reader.Title);
        _bindings.Properties(reader, [nameof(ViewerViewModel.Mode), nameof(ViewerViewModel.OpeningText), nameof(ViewerViewModel.EmptyTitle),
            nameof(ViewerViewModel.EmptyMessage), nameof(ViewerViewModel.EmptyActionText), nameof(ViewerViewModel.EmptySecondaryText)], () =>
        {
            _pages.Hidden = !reader.IsPdf;
            pictureScroll.Hidden = !reader.IsImage;
            _ebook.Hidden = !reader.IsEbook;
            _video.Hidden = !reader.IsVideo;
            message.Hidden = !(reader.IsOpening || reader.ShowEmptyState);
            spinner.Hidden = !reader.IsOpening;
            if (reader.IsOpening) spinner.StartAnimation(null);
            else spinner.StopAnimation(null);
            messageTitle.StringValue = reader.IsOpening ? reader.OpeningText
                : reader.EmptyTitle;
            messageBody.StringValue = reader.ShowEmptyState ? reader.EmptyMessage : "";
            action.Title = reader.EmptyActionText ?? "";
            action.Hidden = !reader.ShowEmptyState || reader.EmptyActionText is null || reader.EmptyCommand is null;
            secondary.Title = reader.EmptySecondaryText ?? "";
            secondary.Hidden = !reader.ShowEmptyState || reader.EmptySecondaryText is null || reader.EmptySecondaryCommand is null;
            foreach (var control in new NSControl[] { previous, next, pageBox, zoomOut, zoomIn, fit, find, findPrevious, findNext })
                control.Hidden = !reader.HasPages;
            fitPage.Hidden = !reader.IsPdf;
            pageCount.Hidden = !reader.HasPages;
        });
        _bindings.Property(reader, nameof(ViewerViewModel.Pdf), ShowPdf);
        DialogSheet.Follow(_bindings, reader.Sessions, () => View.Window);
        _bindings.Property(reader, nameof(ViewerViewModel.Image), () =>
        {
            picture.Image = (reader.Image as NSImage)!;
            if (reader.Image is NSImage image) picture.SetFrameSize(image.Size);
        });
        _bindings.Property(reader, nameof(ViewerViewModel.Zoom), () =>
        {
            _pages.SetZoom(reader.Zoom);
            _ebook.SetZoom(reader.Zoom > 0 ? reader.Zoom : 1, fitPage: reader.Zoom == ZoomSteps.FitPage);
        });
        _bindings.Property(reader, nameof(ViewerViewModel.Ebook), ShowEbook);
        _bindings.Property(reader, nameof(ViewerViewModel.Video), () => _video.Show(reader.Video));
        reader.EbookFind = _ebook.FindAsync;
        reader.EbookTerms = _ebook.MarkTermsAsync;
        EventHandler<int> ebookPage = (_, page) => reader.CurrentPageIndex = page;
        _ebook.CurrentPageChanged += ebookPage;
        EventHandler<int> ebookZoom = (_, step) => reader.StepEbookZoom(step);
        _ebook.ZoomRequested += ebookZoom;
        _bindings.Add(() =>
        {
            _ebook.CurrentPageChanged -= ebookPage;
            _ebook.ZoomRequested -= ebookZoom;
            reader.EbookFind = null;
            reader.EbookTerms = null;
        });
        _bindings.Properties(reader, [nameof(ViewerViewModel.OutlineVisible), nameof(ViewerViewModel.Outline), nameof(ViewerViewModel.ShowOutline)], () =>
        {
            _outline.Hidden = !reader.OutlineVisible;
            outlineToggle.Hidden = !reader.HasOutline || !reader.HasPages;
            _outline.Show(reader.Outline);
        });
        _bindings.Properties(reader, [nameof(ViewerViewModel.StripVisible), nameof(ViewerViewModel.ShowStrip), nameof(ViewerViewModel.Mode)], () =>
        {
            _strip.Hidden = !reader.StripVisible;
            stripToggle.Hidden = !reader.IsPdf;
        });
        _bindings.Properties(reader, [nameof(ViewerViewModel.AskVisible), nameof(ViewerViewModel.Mode)], () =>
        {
            _askPane.Hidden = !reader.AskVisible || !reader.HasPages;
            askToggle.Hidden = !reader.HasPages;
        });
        _bindings.Properties(reader, [nameof(ViewerViewModel.NotesVisible), nameof(ViewerViewModel.Mode)], () =>
        {
            addToBinder.Hidden = !reader.CanAddToBinder;
            _notesPane.Hidden = !reader.NotesVisible || !reader.HasPages;
            notesToggle.Hidden = !reader.HasPages;
        });
        _bindings.Properties(reader.Strip, [nameof(PageStripViewModel.Pages), nameof(PageStripViewModel.Current)], () => _strip.Show());
        _bindings.Property(reader, nameof(ViewerViewModel.PageEntry), () =>
        {
            if (pageBox.CurrentEditor is null) pageBox.StringValue = reader.PageEntry;
        });
        _bindings.Property(reader, nameof(ViewerViewModel.PageCountText), () => pageCount.StringValue = reader.PageCountText);
        _bindings.Property(reader, nameof(ViewerViewModel.Notice), () =>
        {
            notice.StringValue = reader.Notice ?? "";
            notice.Hidden = reader.Notice is null;
        });
        _bindings.Command(previous, reader.PreviousPageCommand);
        _bindings.Command(next, reader.NextPageCommand);
        _bindings.Command(fit, reader.ResetZoomCommand);
        pageBox.Activated += (_, _) =>
        {
            reader.PageEntry = pageBox.StringValue;
            reader.GoToPageCommand.Execute(null);
        };

        var findModel = reader.Find;
        find.Activated += (_, _) =>
        {
            findModel.Query = find.StringValue;
            if (findModel.FindCommand.CanExecute(null)) findModel.FindCommand.Execute(null);
        };
        _bindings.Command(findNext, findModel.NextCommand);
        _bindings.Command(findPrevious, findModel.PreviousCommand);
        _bindings.Property(findModel, nameof(FindViewModel.Status), () => findStatus.StringValue = findModel.Status);
        _bindings.Property(findModel, nameof(FindViewModel.Highlights), () =>
        {
            var highlights = findModel.Highlights;
            if (_shownEbook is not null) _ebook.ShowMatches(highlights.Items.Count == 0 ? null : findModel.ShowsTerms ? "terms" : "find", highlights.Current);
            else _pages.ShowHighlights(highlights.Items, highlights.Current);
        });

        EventHandler<int> moved = (_, page) => reader.CurrentPageIndex = page;
        _pages.CurrentPageChanged += moved;
        _bindings.Add(() => _pages.CurrentPageChanged -= moved);
        EventHandler<PageTarget> goTo = (_, target) => Bindings.OnMain(() =>
        {
            if (reader.IsEbook || _shownEbook is not null) _ebook.GoTo(target.PageIndex);
            else _pages.GoToPage(target.PageIndex, target.PdfTop);
        });
        reader.GoToRequested += goTo;
        _bindings.Add(() => reader.GoToRequested -= goTo);
    }

    void ShowPdf()
    {
        var pdf = reader.Pdf;
        if (ReferenceEquals(pdf, _shownPdf)) return;
        _shownPdf = pdf;
        if (pdf?.Document is MacReaderDocument document)
        {
            _pages.SetZoom(reader.Zoom);
            _pages.Load(document, pdf.Doc, pdf.PageIndex, pdf.PdfTop);
            View.Window?.MakeFirstResponder(_pages.DocumentView);
        }
        else _pages.Unload();
    }

    void ShowEbook()
    {
        var ebook = reader.Ebook;
        if (ReferenceEquals(ebook, _shownEbook)) return;
        _shownEbook = ebook;
        if (ebook is null) _ebook.Unload();
        else
        {
            _ebook.SetZoom(reader.Zoom > 0 ? reader.Zoom : 1, fitPage: reader.Zoom == ZoomSteps.FitPage);
            _ebook.Show(ebook.Content, ebook.PageIndex);
        }
    }

    void FitPage()
    {
        if (!reader.IsPdf) return;
        var page = reader.CurrentPageIndex;
        reader.Zoom = ZoomSteps.FitPage;
        _pages.GoToPage(page, null, centred: true);
    }

    void StepZoom(int step)
    {
        if (reader.IsEbook)
        {
            reader.StepEbookZoom(step);
            return;
        }
        var current = reader.Zoom > 0 ? reader.Zoom : _pages.ZoomInUse;
        reader.Zoom = ZoomSteps.Next(current, up: step > 0);
    }

    static void Run(CommunityToolkit.Mvvm.Input.IRelayCommand? command)
    {
        if (command?.CanExecute(null) == true) command.Execute(null);
    }

    static NSButton Button(string symbol, string tip, Action? action)
    {
        var button = NSButton.CreateButton(NSImage.GetSystemSymbol(symbol, tip)!, action ?? (() => { }));
        button.BezelStyle = NSBezelStyle.Toolbar;
        button.ToolTip = tip;
        return button;
    }

    static NSBox Separator()
    {
        var box = new NSBox { BoxType = NSBoxType.NSBoxSeparator };
        box.HeightAnchor.ConstraintEqualTo(18).Active = true;
        return box;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _bindings.Dispose();
            _pages?.Unload();
        }
        base.Dispose(disposing);
    }
}
