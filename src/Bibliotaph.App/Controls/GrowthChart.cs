using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Bibliotaph.App.Controls;

/// <summary>
/// An area chart of a running total, drawn to fit: <see cref="Values"/> are fractions of the chart's height, evenly
/// spaced from left to right. Pointing at it marks the nearest point and shows its line of <see cref="Labels"/>.
/// </summary>
public sealed class GrowthChart : FrameworkElement
{
    const double LineThickness = 2;
    const double Marker = 4;

    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IReadOnlyList<double>), typeof(GrowthChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LabelsProperty = DependencyProperty.Register(
        nameof(Labels), typeof(IReadOnlyList<string>), typeof(GrowthChart), new PropertyMetadata(null));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(GrowthChart),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(GrowthChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GridProperty = DependencyProperty.Register(
        nameof(Grid), typeof(Brush), typeof(GrowthChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    // Not the element's ToolTip: this one follows the pointer, so it opens and moves by hand.
    readonly ToolTip _tip = new() { Placement = System.Windows.Controls.Primitives.PlacementMode.Relative };
    int? _pointed;

    public IReadOnlyList<double>? Values
    {
        get => (IReadOnlyList<double>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public IReadOnlyList<string>? Labels
    {
        get => (IReadOnlyList<string>?)GetValue(LabelsProperty);
        set => SetValue(LabelsProperty, value);
    }

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public Brush? Fill
    {
        get => (Brush?)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    /// <summary>The baseline and the line across the top, recessive behind the data.</summary>
    public Brush? Grid
    {
        get => (Brush?)GetValue(GridProperty);
        set => SetValue(GridProperty, value);
    }

    Point At(int i, int count) =>
        new(count == 1 ? ActualWidth / 2 : ActualWidth * i / (count - 1), LineThickness + (ActualHeight - 2 * LineThickness) * (1 - Values![i]));

    protected override void OnRender(DrawingContext drawingContext)
    {
        drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (Grid is { } grid)
        {
            var rule = new Pen(grid, 1);
            drawingContext.DrawLine(rule, new Point(0, ActualHeight - 0.5), new Point(ActualWidth, ActualHeight - 0.5));
            drawingContext.DrawLine(rule, new Point(0, LineThickness), new Point(ActualWidth, LineThickness));
        }
        if (Values is not { Count: > 0 } values || ActualWidth <= 0 || ActualHeight <= 0) return;

        var points = Enumerable.Range(0, values.Count).Select(i => At(i, values.Count)).ToList();
        var line = new StreamGeometry();
        using (var g = line.Open())
        {
            g.BeginFigure(points[0], false, false);
            g.PolyLineTo(points.Skip(1).ToList(), true, true);
        }
        line.Freeze();
        if (Fill is { } fill)
        {
            var area = new StreamGeometry();
            using (var g = area.Open())
            {
                g.BeginFigure(new Point(points[0].X, ActualHeight), true, true);
                g.PolyLineTo([.. points, new Point(points[^1].X, ActualHeight)], false, true);
            }
            area.Freeze();
            drawingContext.DrawGeometry(fill, null, area);
        }
        drawingContext.DrawGeometry(null, new Pen(Stroke, LineThickness) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, line);
        if (_pointed is { } pointed && pointed < points.Count)
        {
            var guide = new Pen(Grid ?? Stroke, 1);
            drawingContext.DrawLine(guide, new Point(points[pointed].X, 0), new Point(points[pointed].X, ActualHeight));
            drawingContext.DrawEllipse(Stroke, null, points[pointed], Marker, Marker);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (Values is not { Count: > 0 } values) return;
        var x = e.GetPosition(this).X;
        var i = values.Count == 1 ? 0 : (int)Math.Round(Math.Clamp(x / ActualWidth, 0, 1) * (values.Count - 1));
        var point = At(i, values.Count);
        if (_pointed != i)
        {
            _pointed = i;
            _tip.Content = Labels is { } labels && i < labels.Count ? labels[i] : null;
            InvalidateVisual();
        }
        _tip.PlacementTarget = this;
        _tip.HorizontalOffset = point.X + 10;
        _tip.VerticalOffset = point.Y - 34;
        _tip.IsOpen = _tip.Content is not null;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _pointed = null;
        _tip.IsOpen = false;
        InvalidateVisual();
    }
}
