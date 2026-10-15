using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace Bibliotaph.App.Controls;

/// <summary>
/// The line a dragged pin shows where it will land: across the top or the bottom of the item under the mouse, in the
/// accent colour. One at a time; <see cref="Show"/> moves it and <see cref="Hide"/> takes it away.
/// </summary>
public sealed class DropLine : Adorner
{
    static DropLine? _shown;
    readonly bool _below;

    DropLine(UIElement target, bool below) : base(target)
    {
        _below = below;
        IsHitTestVisible = false;
    }

    public static void Show(UIElement target, bool below)
    {
        if (_shown is { } shown && ReferenceEquals(shown.AdornedElement, target) && shown._below == below) return;
        Hide();
        if (AdornerLayer.GetAdornerLayer(target) is not { } layer) return;
        _shown = new DropLine(target, below);
        layer.Add(_shown);
    }

    public static void Hide()
    {
        if (_shown is not { } shown) return;
        _shown = null;
        AdornerLayer.GetAdornerLayer(shown.AdornedElement)?.Remove(shown);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var brush = TryFindResource("Bt.Accent") as Brush ?? SystemColors.HighlightBrush;
        var size = AdornedElement.RenderSize;
        var y = _below ? size.Height - 1 : 1;
        drawingContext.DrawRoundedRectangle(brush, null, new Rect(4, y - 1, Math.Max(0, size.Width - 8), 2), 1, 1);
    }
}
