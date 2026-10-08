using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace Bibliotaph.App.Controls;

/// <summary>
/// Draws one Lucide icon (a Geometry in Lucide's 24x24 space) as round-capped strokes, scaled to
/// <see cref="Size"/> device-independent pixels, in the inherited Foreground. Matches the mockup:
/// 17px icons with a 1.65 stroke in the 24-unit space.
/// </summary>
public sealed class LucideIcon : FrameworkElement
{
    const double Canvas = 24;

    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(Geometry), typeof(LucideIcon),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(LucideIcon),
        new FrameworkPropertyMetadata(17d, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(LucideIcon),
        new FrameworkPropertyMetadata(1.65, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(LucideIcon),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    static LucideIcon()
    {
        FocusableProperty.OverrideMetadata(typeof(LucideIcon), new FrameworkPropertyMetadata(false));
        SnapsToDevicePixelsProperty.OverrideMetadata(typeof(LucideIcon), new FrameworkPropertyMetadata(true));
    }

    public Geometry? Data
    {
        get => (Geometry?)GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public double Size
    {
        get => (double)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    protected override System.Windows.Size MeasureOverride(System.Windows.Size availableSize) => new(Size, Size);

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (Data is null) return;
        var pen = new Pen(Foreground, StrokeThickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        pen.Freeze();
        var scale = Size / Canvas;
        drawingContext.PushTransform(new ScaleTransform(scale, scale));
        drawingContext.DrawGeometry(null, pen, Data);
        drawingContext.Pop();
    }
}
