using AppKit;
using Bibliotaph.Core;
using Bibliotaph.Mac.Binding;
using Bibliotaph.Presentation.ViewModels;
using CoreGraphics;

namespace Bibliotaph.Mac.Views;

/// <summary>
/// Summary: the library by the numbers, laid out from the page both apps share. What you own, where it came from, what
/// it is and what you've done with it, each figure opening the Library at the books it counts. Bars show what's
/// confirmed in the accent and what's only suggested in a lighter step of it, as on Windows.
/// </summary>
public sealed class SummaryController(SummaryViewModel summary) : NSViewController
{
    readonly Bindings _bindings = new();
    NSStackView _content = null!;

    public override void LoadView()
    {
        var (root, content) = PageParts.ScrollingPage();
        _content = content;
        content.Spacing = 10;
        View = root;
        _bindings.Property(summary, nameof(SummaryViewModel.Page), Build);
    }

    void Build()
    {
        foreach (var view in _content.ArrangedSubviews) view.RemoveFromSuperview();
        if (summary.Page is not { } page) return;
        if (!page.HasBooks)
        {
            Add(PageParts.Secondary("Once Bibliotaph has read the folders where your books are, what you own is summed up here."));
            Add(NSButton.CreateButton("Manage Folders…", () => summary.ManageFoldersCommand.Execute(null)));
            return;
        }

        // What you own.
        Add(Eyebrow("The Hoard"));
        var owned = Label($"You own {page.BooksText}, {page.PagesText} and {page.SizeText} of RPG material.", 20, NSFontWeight.Semibold);
        Add(new Figure(owned, () => Open(SummaryViewModel.Everything), "Every book in the Library"), stretch: true);
        if (page.ReadingTime is { } time) Add(Secondary(time), stretch: true);
        Add(Tiles(page.Hoard), stretch: true);

        // Where it came from.
        Space(14);
        Add(Eyebrow("Where It Came From"));
        var sources = page.Sources is { } listed
            ? List(listed, "Import More…", () => summary.ImportMoreCommand.Execute(null), page.StoresText)
            : Column(ListHead("Online libraries and stores", "Import More…", () => summary.ImportMoreCommand.Execute(null)),
                Secondary("No book says where else you own it, or where it came from, yet."));
        Add(Columns(
            page.Folders is { } folders ? List(folders, "Manage Folders…", () => summary.ManageFoldersCommand.Execute(null), page.OfflineText) : null,
            sources), stretch: true);
        Add(Columns(List(page.Publishers), List(page.FoundOnline)), stretch: true);

        // What it is.
        Space(14);
        Add(Row(Eyebrow("What It Is"), Legend()));
        Add(Columns(List(page.Systems), List(page.Types), List(page.Settings)), stretch: true);
        Add(Columns(List(page.Themes), List(page.Environments), List(page.Includes)), stretch: true);
        Add(Columns(
            Strip("By character level", page.Levels, page.LevelsText, "No book or adventure has its levels yet.",
                page.LevelsUnknownText is { } unknown ? Link(unknown, () => Open(page.LevelsUnknownLink)) : null),
            Strip("Published, by decade", page.Decades, page.YearsText, "No book has its year yet.",
                page.YearUnknownText is { } years ? Secondary(years) : null)), stretch: true);

        // What you've done with it.
        Space(14);
        Add(Eyebrow("What You’ve Done With It"));
        Add(Label(page.OpenedText, 24, NSFontWeight.Semibold), stretch: true);
        Add(Link(page.WaitingText, () => Open(SummaryViewModel.NeverOpened)));
        var opened = new BarView(page.OpenedShare, page.OpenedShare, height: 10) { Track = true };
        Add(opened);
        opened.WidthAnchor.ConstraintLessThanOrEqualTo(620).Active = true;
        var openedWidth = opened.WidthAnchor.ConstraintEqualTo(_content.WidthAnchor, 1, -48);
        // Below the split view's own holding priorities, so the bar follows the page's width and never sets it.
        openedWidth.Priority = 200;
        openedWidth.Active = true;
        Add(Tiles(page.Work), stretch: true);
        Add(Secondary(page.ReadingText), stretch: true);
        if (page.HasGrowth)
        {
            Space(10);
            Add(Label("Growth Since Your First Book", 13, NSFontWeight.Semibold));
            var chart = new GrowthChart(page.Growth, page.GrowthLabels);
            chart.HeightAnchor.ConstraintEqualTo(140).Active = true;
            Add(chart, stretch: true);
            var ticks = new NSStackView { Distribution = NSStackViewDistribution.EqualCentering };
            foreach (var tick in new[] { page.GrowthStart, page.GrowthMiddle, page.GrowthEnd }) ticks.AddArrangedSubview(Small(tick));
            Add(ticks, stretch: true);
            if (page.BiggestDayText is { } biggest) Add(Secondary(biggest), stretch: true);
        }
    }

    void Open(SummaryLink? link)
    {
        if (link is not null) summary.OpenCommand.Execute(link);
    }

    /// <summary>Tiles of counts, as many to a row as fit: "312 Adventures", each opening the books it counts.</summary>
    WrapView Tiles(IReadOnlyList<SummaryTile> tiles)
    {
        var wrap = new WrapView { Gap = 10 };
        wrap.SetItems(tiles.Select(tile =>
        {
            var count = Label(tile.Count, 22, NSFontWeight.Semibold);
            var parts = Column(count, Small(tile.Label));
            parts.Spacing = 1;
            if (tile.Detail is { } detail) parts.AddArrangedSubview(Small(detail));
            var box = new Tinted(parts, Palette.Panel, 10, new NSEdgeInsets(10, 14, 10, 14));
            box.WidthAnchor.ConstraintGreaterThanOrEqualTo(120).Active = true;
            var figure = new Figure(box, tile.Link is { } link ? () => Open(link) : null, tile.Label);
            figure.SetFrameSize(figure.FittingSize);
            return (NSView)figure;
        }));
        return wrap;
    }

    /// <summary>A list of bars under its heading, with its note or a link beside the heading and a line under it.</summary>
    NSStackView? List(SummaryList? list, string? action = null, Action? act = null, string? under = null)
    {
        if (list is null) return null;
        var column = Column(ListHead(list.Heading, action, act, list.Note));
        column.Spacing = 5;
        foreach (var bar in list.Bars)
        {
            var name = NSTextField.CreateLabel(bar.Label);
            name.Font = NSFont.SystemFontOfSize(12)!;
            name.LineBreakMode = NSLineBreakMode.TruncatingTail;
            name.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
            var count = Small(bar.Count);
            count.Alignment = NSTextAlignment.Right;
            count.WidthAnchor.ConstraintGreaterThanOrEqualTo(36).Active = true;
            var row = Row(name, new BarView(bar.Confirmed, bar.Total, height: 8), count);
            row.Distribution = NSStackViewDistribution.Fill;
            row.Spacing = 8;
            if (bar.Offline) row.AlphaValue = 0.5f;
            var figure = new Figure(row, bar.Link is { } link ? () => Open(link) : null, bar.ToolTip);
            column.AddArrangedSubview(figure);
            figure.WidthAnchor.ConstraintEqualTo(column.WidthAnchor).Active = true;
            name.WidthAnchor.ConstraintEqualTo(column.WidthAnchor, 0.5f).Active = true;
        }
        if (under is not null) column.AddArrangedSubview(Secondary(under));
        return column;
    }

    static NSStackView ListHead(string heading, string? action, Action? act, string? note = null)
    {
        var title = Label(MacText.TitleCase(heading), 13, NSFontWeight.Semibold);
        title.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
        var row = Row(title);
        row.Distribution = NSStackViewDistribution.Fill;
        if (note is not null) row.AddArrangedSubview(Small(note));
        if (action is not null && act is not null) row.AddArrangedSubview(Link(action, act));
        return row;
    }

    /// <summary>A row of columns, a level or a decade each, standing on one baseline with its tick under it.</summary>
    NSStackView Strip(string heading, IReadOnlyList<SummaryColumn> columns, string? text, string none, NSView? beside)
    {
        var head = Row(Label(MacText.TitleCase(heading), 13, NSFontWeight.Semibold));
        head.Distribution = NSStackViewDistribution.Fill;
        ((NSTextField)head.ArrangedSubviews[0]).SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
        if (beside is not null) head.AddArrangedSubview(beside);
        var column = Column(head);
        if (text is null)
        {
            column.AddArrangedSubview(Secondary(none));
            return column;
        }
        var strip = new NSStackView { Distribution = NSStackViewDistribution.FillEqually, Spacing = 4, Alignment = NSLayoutAttribute.Bottom };
        foreach (var item in columns)
        {
            var bar = new ColumnBar(item.Share);
            bar.HeightAnchor.ConstraintEqualTo(72).Active = true;
            var tick = Small(item.Tick);
            tick.Font = NSFont.SystemFontOfSize(10)!;
            tick.Alignment = NSTextAlignment.Center;
            var stack = Column(bar, tick);
            stack.Alignment = NSLayoutAttribute.CenterX;
            stack.Spacing = 4;
            bar.WidthAnchor.ConstraintEqualTo(stack.WidthAnchor).Active = true;
            strip.AddArrangedSubview(new Figure(stack, item.Link is { } link ? () => Open(link) : null, item.ToolTip));
        }
        column.AddArrangedSubview(strip);
        // As wide as the column, but no column of it wider than a bar should be, so a few decades don't make blocks.
        strip.WidthAnchor.ConstraintLessThanOrEqualTo(column.WidthAnchor).Active = true;
        strip.WidthAnchor.ConstraintLessThanOrEqualTo(columns.Count * 56).Active = true;
        var fill = strip.WidthAnchor.ConstraintEqualTo(column.WidthAnchor);
        // Below the split view's holding priorities: a strip of a few decades must not narrow the page to fit it.
        fill.Priority = 200;
        fill.Active = true;
        column.AddArrangedSubview(Secondary(text));
        return column;
    }

    /// <summary>Lists side by side in equal columns; one that's missing leaves its place empty, as on Windows.</summary>
    static NSStackView Columns(params NSStackView?[] lists)
    {
        var row = new NSStackView { Distribution = NSStackViewDistribution.FillEqually, Spacing = 32, Alignment = NSLayoutAttribute.Top };
        foreach (var list in lists) row.AddArrangedSubview((NSView?)list ?? new NSView());
        foreach (var view in row.ArrangedSubviews)
        {
            view.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
            // Each list as tall as its own lines: a shorter list stretched to the row's height spread its lines out,
            // dropping its heading below its neighbours'.
            if (view is NSStackView list) list.SetHuggingPriority(750, NSLayoutConstraintOrientation.Vertical);
        }
        // Equal however narrow a column's contents would let it be.
        foreach (var view in row.ArrangedSubviews.Skip(1)) view.WidthAnchor.ConstraintEqualTo(row.ArrangedSubviews[0].WidthAnchor).Active = true;
        return row;
    }

    static NSStackView Legend()
    {
        NSView Swatch(NSColor colour)
        {
            var swatch = new Tinted(new NSView(), colour, 2, new NSEdgeInsets(0, 0, 0, 0));
            swatch.WidthAnchor.ConstraintEqualTo(10).Active = true;
            swatch.HeightAnchor.ConstraintEqualTo(10).Active = true;
            return swatch;
        }
        var legend = Row(Swatch(NSColor.ControlAccent), Small("Confirmed"), Swatch(BarView.Suggested), Small("Suggested by AI or lookups"));
        legend.Spacing = 5;
        legend.SetCustomSpacing(16, legend.ArrangedSubviews[1]);
        return legend;
    }

    static NSTextField Eyebrow(string text)
    {
        var label = Label(text, 15, NSFontWeight.Bold);
        return label;
    }

    static NSButton Link(string title, Action open)
    {
        var link = NSButton.CreateButton(title, open);
        link.Bordered = false;
        link.ContentTintColor = NSColor.ControlAccent;
        link.Font = NSFont.SystemFontOfSize(12)!;
        return link;
    }

    static NSTextField Label(string text, nfloat size, nfloat weight)
    {
        var label = NSTextField.CreateWrappingLabel(text);
        label.Font = NSFont.SystemFontOfSize(size, weight)!;
        return label;
    }

    static NSTextField Secondary(string text)
    {
        var label = NSTextField.CreateWrappingLabel(text);
        label.Font = NSFont.SystemFontOfSize(12)!;
        label.TextColor = NSColor.SecondaryLabel;
        return label;
    }

    static NSTextField Small(string text)
    {
        var label = NSTextField.CreateLabel(text);
        label.Font = NSFont.SystemFontOfSize(11)!;
        label.TextColor = NSColor.SecondaryLabel;
        return label;
    }

    static NSStackView Row(params NSView[] views)
    {
        var row = NSStackView.FromViews(views);
        row.Spacing = 8;
        return row;
    }

    static NSStackView Column(params NSView[] views)
    {
        var column = NSStackView.FromViews(views);
        column.Orientation = NSUserInterfaceLayoutOrientation.Vertical;
        column.Alignment = NSLayoutAttribute.Leading;
        column.Spacing = 8;
        return column;
    }

    void Space(nfloat points)
    {
        if (_content.ArrangedSubviews.LastOrDefault() is { } last) _content.SetCustomSpacing(10 + points, last);
    }

    void Add(NSView view, bool stretch = false)
    {
        _content.AddArrangedSubview(view);
        if (stretch) view.WidthAnchor.ConstraintEqualTo(_content.WidthAnchor, 1, -48).Active = true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _bindings.Dispose();
        base.Dispose(disposing);
    }

    /// <summary>A figure that opens the books it counts: no button chrome, the pointing hand only when it goes somewhere.</summary>
    sealed class Figure : NSView
    {
        readonly bool _opens;

        public Figure(NSView content, Action? open, string? tip)
        {
            _opens = open is not null;
            TranslatesAutoresizingMaskIntoConstraints = false;
            content.TranslatesAutoresizingMaskIntoConstraints = false;
            AddSubview(content);
            NSLayoutConstraint.ActivateConstraints(
            [
                content.TopAnchor.ConstraintEqualTo(TopAnchor),
                content.BottomAnchor.ConstraintEqualTo(BottomAnchor),
                content.LeadingAnchor.ConstraintEqualTo(LeadingAnchor),
                content.TrailingAnchor.ConstraintEqualTo(TrailingAnchor),
            ]);
            if (open is not null) AddGestureRecognizer(new NSClickGestureRecognizer(open));
            if (tip is not null) ToolTip = tip;
        }

        public override void ResetCursorRects()
        {
            if (_opens) AddCursorRect(Bounds, NSCursor.PointingHandCursor);
        }
    }

    /// <summary>A bar: the confirmed share in the accent over the suggested share in a lighter step of it.</summary>
    sealed class BarView(double confirmed, double total, nfloat height) : NSView
    {
        public static NSColor Suggested => NSColor.ControlAccent.ColorWithAlphaComponent(0.35f);

        /// <summary>The whole length drawn faintly under the bar, for a share of everything.</summary>
        public bool Track { get; init; }

        public override CGSize IntrinsicContentSize => new(NSView.NoIntrinsicMetric, height);

        public override void DrawRect(CGRect dirtyRect)
        {
            var y = (Bounds.Height - height) / 2;
            void Fill(double share, NSColor colour)
            {
                if (share <= 0) return;
                colour.SetFill();
                NSBezierPath.FromRoundedRect(new CGRect(0, y, Math.Max(height, Bounds.Width * Math.Min(1, share)), height), height / 2, height / 2).Fill();
            }
            if (Track) Fill(1, Palette.Soft);
            Fill(total, Suggested);
            Fill(confirmed, NSColor.ControlAccent);
        }
    }

    /// <summary>One column of a strip, standing on the baseline as tall as its share of the tallest.</summary>
    sealed class ColumnBar(double share) : NSView
    {
        public override void DrawRect(CGRect dirtyRect)
        {
            var height = Math.Max(share > 0 ? 2 : 0, Bounds.Height * share);
            NSColor.ControlAccent.SetFill();
            NSBezierPath.FromRoundedRect(new CGRect(0, 0, Bounds.Width, height), 2, 2).Fill();
        }
    }

    /// <summary>Books in the library month by month, as a line with the area under it lightly filled.</summary>
    /// <summary>
    /// The library's growth as a line, filled under. Pointing at it marks the nearest day and says what it was, as
    /// Windows' chart does with its labels.
    /// </summary>
    sealed class GrowthChart : NSView
    {
        readonly IReadOnlyList<double> _values;
        readonly IReadOnlyList<string> _labels;
        int? _at;

        public GrowthChart(IReadOnlyList<double> values, IReadOnlyList<string> labels)
        {
            _values = values;
            _labels = labels;
            AddTrackingArea(new NSTrackingArea(CGRect.Empty,
                NSTrackingAreaOptions.MouseMoved | NSTrackingAreaOptions.MouseEnteredAndExited | NSTrackingAreaOptions.ActiveInKeyWindow | NSTrackingAreaOptions.InVisibleRect,
                this, null));
        }

        CGRect Area => Bounds.Inset(1, 2);

        CGPoint At(int i)
        {
            var top = Math.Max(1, _values.Max());
            var bounds = Area;
            return new(bounds.X + bounds.Width * i / (_values.Count - 1), bounds.Y + (bounds.Height - 18) * _values[i] / top);
        }

        public override void MouseMoved(NSEvent theEvent)
        {
            if (_values.Count < 2) return;
            var x = ConvertPointFromView(theEvent.LocationInWindow, null).X;
            var at = (int)Math.Round((x - Area.X) / Area.Width * (_values.Count - 1));
            at = Math.Clamp(at, 0, _values.Count - 1);
            if (at == _at) return;
            _at = at;
            NeedsDisplay = true;
        }

        public override void MouseExited(NSEvent theEvent)
        {
            _at = null;
            NeedsDisplay = true;
        }

        public override void DrawRect(CGRect dirtyRect)
        {
            if (_values.Count < 2) return;
            var bounds = Area;
            var line = new NSBezierPath();
            line.MoveTo(At(0));
            for (var i = 1; i < _values.Count; i++) line.LineTo(At(i));
            var area = (NSBezierPath)line.Copy();
            area.LineTo(new CGPoint(bounds.Right, bounds.Y));
            area.LineTo(new CGPoint(bounds.X, bounds.Y));
            area.ClosePath();
            NSColor.ControlAccent.ColorWithAlphaComponent(0.15f).SetFill();
            area.Fill();
            NSColor.ControlAccent.SetStroke();
            line.LineWidth = 2;
            line.LineJoinStyle = NSLineJoinStyle.Round;
            line.Stroke();
            if (_at is not { } at) return;
            var point = At(at);
            NSColor.ControlAccent.SetFill();
            NSBezierPath.FromOvalInRect(new CGRect(point.X - 4, point.Y - 4, 8, 8)).Fill();
            if (at >= _labels.Count) return;
            var text = new NSAttributedString(_labels[at], new NSStringAttributes { Font = NSFont.SystemFontOfSize(11, NSFontWeight.Medium)!, ForegroundColor = NSColor.Label }.Dictionary);
            var size = text.Size;
            var x = Math.Clamp(point.X - size.Width / 2, bounds.X, bounds.Right - size.Width);
            text.DrawAtPoint(new CGPoint(x, Math.Min(point.Y + 6, bounds.Bottom - size.Height)));
        }
    }
}
