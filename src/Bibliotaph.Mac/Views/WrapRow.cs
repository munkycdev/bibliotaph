using AppKit;
using CoreGraphics;

namespace Bibliotaph.Mac.Views;

/// <summary>
/// A row of controls that carries on in a line below when there isn't room for it, as Windows' WrapPanel does. A
/// stack view of the same controls can't be narrower than all of them side by side, and AppKit keeps the window at
/// least that wide; this row asks for one line's width but gives way to the window, so a long row of filters never
/// sets how narrow the window can be. Hidden views take no room.
/// </summary>
sealed class WrapRow : NSView
{
    readonly List<Placed> _views = [];
    nfloat _height;
    nfloat _oneLine;

    public WrapRow(double spacing, double lineSpacing = 6)
    {
        Spacing = (nfloat)spacing;
        LineSpacing = (nfloat)lineSpacing;
        TranslatesAutoresizingMaskIntoConstraints = false;
        // Below the window's own hold on its size, so the window, not this row, decides the width.
        SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        SetContentHuggingPriorityForOrientation(250, NSLayoutConstraintOrientation.Horizontal);
        SetContentHuggingPriorityForOrientation(750, NSLayoutConstraintOrientation.Vertical);
        SetContentCompressionResistancePriority(750, NSLayoutConstraintOrientation.Vertical);
    }

    public nfloat Spacing { get; }

    public nfloat LineSpacing { get; }

    /// <summary>Each line's views stand on its top rather than centred in it, for cards of different heights.</summary>
    public bool AlignTops { get; init; }

    public IEnumerable<NSView> Views => _views.Select(p => p.View);

    public void Add(NSView view, nfloat? spacingAfter = null)
    {
        view.TranslatesAutoresizingMaskIntoConstraints = false;
        AddSubview(view);
        var x = view.LeadingAnchor.ConstraintEqualTo(LeadingAnchor);
        var y = view.TopAnchor.ConstraintEqualTo(TopAnchor);
        x.Active = y.Active = true;
        // Showing or hiding a view moves the ones after it, which AppKit doesn't ask the row to lay out for.
        var watch = view.AddObserver("hidden", Foundation.NSKeyValueObservingOptions.New, _ => NeedsLayout = true);
        _views.Add(new Placed(view, x, y, watch, spacingAfter));
        NeedsLayout = true;
    }

    public void AddRange(IEnumerable<NSView> views)
    {
        foreach (var view in views) Add(view);
    }

    public void RemoveAll()
    {
        foreach (var placed in _views)
        {
            placed.Watch.Dispose();
            placed.View.RemoveFromSuperview();
        }
        _views.Clear();
        NeedsLayout = true;
    }

    public override CGSize IntrinsicContentSize => new(_oneLine, _height);

    public override void Layout()
    {
        Arrange();
        base.Layout();
    }

    /// <summary>
    /// Places each shown view after the one before it, starting a line when the next won't fit, and asks for the height
    /// the lines take. A view's own size changing lays the row out again, as AppKit sizes subviews in their superview's
    /// layout.
    /// </summary>
    void Arrange()
    {
        var shown = _views.Where(p => !p.View.Hidden).Select(p => (Placed: p, Size: p.View.FittingSize)).ToList();
        nfloat oneLine = 0;
        for (var i = 0; i < shown.Count; i++)
            oneLine += shown[i].Size.Width + (i < shown.Count - 1 ? Gap(shown[i].Placed) : 0);
        var width = Bounds.Width > 0 ? Bounds.Width : oneLine;

        var lines = new List<List<(Placed Placed, CGSize Size)>>();
        List<(Placed Placed, CGSize Size)>? line = null;
        nfloat x = 0;
        foreach (var item in shown)
        {
            var gap = line is { Count: > 0 } ? Gap(line[^1].Placed) : 0;
            if (line is null || (line.Count > 0 && x + gap + item.Size.Width > width))
            {
                line = [];
                lines.Add(line);
                x = 0;
                gap = 0;
            }
            line.Add(item);
            x += gap + item.Size.Width;
        }

        nfloat top = 0;
        foreach (var each in lines)
        {
            var tallest = each.Max(i => i.Size.Height);
            nfloat left = 0;
            for (var i = 0; i < each.Count; i++)
            {
                var (placed, size) = each[i];
                if (i > 0) left += Gap(each[i - 1].Placed);
                Set(placed.X, left);
                Set(placed.Y, top + (AlignTops ? 0 : (nfloat)Math.Floor((tallest - size.Height) / 2)));
                left += size.Width;
            }
            top += tallest + LineSpacing;
        }
        var height = lines.Count > 0 ? top - LineSpacing : 0;
        if (height != _height || oneLine != _oneLine)
        {
            _height = height;
            _oneLine = oneLine;
            InvalidateIntrinsicContentSize();
        }
    }

    nfloat Gap(Placed placed) => placed.SpacingAfter ?? Spacing;

    static void Set(NSLayoutConstraint constraint, nfloat value)
    {
        if (constraint.Constant != value) constraint.Constant = value;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            foreach (var placed in _views) placed.Watch.Dispose();
        base.Dispose(disposing);
    }

    sealed record Placed(NSView View, NSLayoutConstraint X, NSLayoutConstraint Y, IDisposable Watch, nfloat? SpacingAfter);
}
