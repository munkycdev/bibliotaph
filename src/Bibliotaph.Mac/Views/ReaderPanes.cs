using AppKit;
using Bibliotaph.Mac.Binding;
using Bibliotaph.Presentation.ViewModels;
using CoreGraphics;
using Foundation;
using ObjCRuntime;

namespace Bibliotaph.Mac.Views;

/// <summary>The book's bookmarks beside its pages, indented by level, each going to its page with a click.</summary>
sealed class OutlineList : NSScrollView
{
    readonly ViewerViewModel _reader;
    readonly NSTableView _table;
    IReadOnlyList<OutlineEntry> _entries = [];

    public OutlineList(ViewerViewModel reader)
    {
        _reader = reader;
        _table = new NSTableView { Style = NSTableViewStyle.SourceList, HeaderView = null!, UsesAutomaticRowHeights = true };
        _table.AddColumn(new NSTableColumn("entry"));
        var source = new Source(this);
        _table.DataSource = source;
        _table.Delegate = source;
        DocumentView = _table;
        HasVerticalScroller = true;
        DrawsBackground = false;
    }

    public OutlineList(NativeHandle handle) : base(handle)
    {
        _reader = null!;
        _table = null!;
    }

    public void Show(IReadOnlyList<OutlineEntry> entries)
    {
        if (ReferenceEquals(entries, _entries)) return;
        _entries = entries;
        _table.ReloadData();
    }

    sealed class Source(OutlineList owner) : NSTableViewDelegate, INSTableViewDataSource
    {
        [Export("numberOfRowsInTableView:")]
        public nint GetRowCount(NSTableView tableView) => owner._entries.Count;

        public override NSView GetViewForItem(NSTableView tableView, NSTableColumn tableColumn, nint row)
        {
            var entry = owner._entries[(int)row];
            var title = NSTextField.CreateWrappingLabel(entry.Title);
            title.Font = NSFont.SystemFontOfSize(12, entry.Depth == 0 ? NSFontWeight.Medium : NSFontWeight.Regular)!;
            title.MaximumNumberOfLines = 2;
            var page = NSTextField.CreateLabel(entry.Page);
            page.Font = NSFont.MonospacedDigitSystemFontOfSize(11, NSFontWeight.Regular)!;
            page.TextColor = NSColor.SecondaryLabel;
            var line = NSStackView.FromViews([title, page]);
            line.Spacing = 6;
            line.EdgeInsets = new NSEdgeInsets(3, (nfloat)(4 + entry.Indent), 3, 4);
            title.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
            return line;
        }

        public override void SelectionDidChange(NSNotification notification)
        {
            var row = (int)owner._table.SelectedRow;
            if (row < 0 || row >= owner._entries.Count) return;
            owner._reader.OpenOutlineEntryCommand.Execute(owner._entries[row]);
        }
    }
}

/// <summary>
/// The page strip under the pages: a thumbnail of every page side by side, drawn as they come into view, the page
/// being read marked and kept in sight. A click goes to that page.
/// </summary>
sealed class StripView : NSScrollView
{
    const double Gap = 10;
    readonly PageStripViewModel _strip;
    readonly StripDocument _document = new();
    readonly Bindings _pages = new();
    readonly List<NSImageView> _images = [];
    IReadOnlyList<PageThumbnail> _shown = [];

    public StripView(PageStripViewModel strip)
    {
        _strip = strip;
        DocumentView = _document;
        HasHorizontalScroller = true;
        HasVerticalScroller = false;
        AutohidesScrollers = true;
        DrawsBackground = true;
        BackgroundColor = NSColor.UnderPageBackground;
        ContentView.PostsBoundsChangedNotifications = true;
        NSNotificationCenter.DefaultCenter.AddObserver(NSView.BoundsChangedNotification, _ => Draw(), ContentView);
    }

    public StripView(NativeHandle handle) : base(handle)
    {
        _strip = null!;
    }

    public void Show()
    {
        if (!ReferenceEquals(_shown, _strip.Pages)) Build();
        foreach (var (image, page) in _images.Zip(_shown)) image.Layer!.BorderWidth = page.IsCurrent ? 3 : 0;
        if (_strip.Current is { } current && current.Index < _images.Count)
        {
            var frame = _images[current.Index].Frame;
            if (frame.X < ContentView.Bounds.X || frame.Right > ContentView.Bounds.Right) _document.ScrollRectToVisible(frame.Inset(-40, 0));
        }
        Draw();
    }

    void Build()
    {
        _pages.Dispose();
        foreach (var image in _images) image.Superview?.RemoveFromSuperview();
        _images.Clear();
        _shown = _strip.Pages;
        double x = Gap;
        foreach (var page in _shown)
        {
            var image = new NSImageView { ImageScaling = NSImageScale.ProportionallyUpOrDown, WantsLayer = true, ToolTip = page.Page };
            image.Layer!.BackgroundColor = NSColor.White.CGColor;
            image.Layer.BorderColor = NSColor.ControlAccent.CGColor;
            var label = NSTextField.CreateLabel(page.Page);
            label.Font = NSFont.SystemFontOfSize(10)!;
            label.TextColor = NSColor.SecondaryLabel;
            label.Alignment = NSTextAlignment.Center;
            var cell = new ClickView(() => _strip.GoToPageCommand.Execute(page));
            cell.AddSubview(image);
            cell.AddSubview(label);
            cell.Frame = new CGRect(x, 8, page.Width, page.Height + 20);
            image.Frame = new CGRect(0, 0, page.Width, page.Height);
            label.Frame = new CGRect(0, page.Height + 2, page.Width, 16);
            _document.AddSubview(cell);
            _images.Add(image);
            x += page.Width + Gap;
            var thumbnail = page;
            _pages.Property(page, nameof(PageThumbnail.Image), () => image.Image = (thumbnail.Image as NSImage)!);
        }
        _document.SetFrameSize(new CGSize(Math.Max(x, ContentView.Bounds.Width), 140));
    }

    /// <summary>The thumbnails in view, and a few either side, are drawn.</summary>
    void Draw()
    {
        if (Hidden || _images.Count == 0 || ContentView.Bounds.Width <= 0) return;
        var visible = ContentView.Bounds;
        var first = _images.FindIndex(i => i.Superview!.Frame.Right >= visible.X);
        var last = _images.FindLastIndex(i => i.Superview!.Frame.X <= visible.Right);
        if (first >= 0 && last >= first) _strip.Draw(first, last);
    }

    /// <summary>A strip shown before it had a size draws once it has one.</summary>
    public override void Layout()
    {
        base.Layout();
        Draw();
    }

    public override bool Hidden
    {
        get => base.Hidden;
        set
        {
            base.Hidden = value;
            if (!value) Bindings.OnMain(Draw);
        }
    }

    sealed class StripDocument : NSView
    {
        public override bool IsFlipped => true;
    }

    sealed class ClickView(Action click) : NSView
    {
        public override bool IsFlipped => true;

        public override void MouseUp(NSEvent theEvent) => click();
    }
}
