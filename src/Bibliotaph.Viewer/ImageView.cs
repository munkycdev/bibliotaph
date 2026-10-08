using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Bibliotaph.Viewer;

/// <summary>
/// A map or handout image with zoom and pan: fitted to the view at first, Ctrl+wheel or <see cref="Zoom"/> to zoom,
/// drag to pan. The image arrives decoded at a capped size, so a huge scan can't fill memory.
/// </summary>
public sealed class ImageView : ContentControl
{
    static readonly double[] ZoomSteps = [0.25, 0.5, 0.75, 1, 1.25, 1.5, 2, 3, 4];

    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(nameof(Source), typeof(ImageSource), typeof(ImageView),
        new PropertyMetadata(null, (d, _) => ((ImageView)d).Relayout()));

    public static readonly DependencyProperty ZoomProperty = DependencyProperty.Register(nameof(Zoom), typeof(double), typeof(ImageView),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((ImageView)d).Relayout()));

    readonly ScrollViewer _scroll;
    readonly Image _image;
    Point? _dragFrom;
    Point _dragOffset;

    public ImageView()
    {
        _image = new Image { Stretch = Stretch.Fill, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        _scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _image,
            Cursor = Cursors.Hand,
            Focusable = true,
        };
        AutomationProperties.SetName(_scroll, "Image");
        Content = _scroll;
        SizeChanged += (_, _) => Relayout();
        _scroll.PreviewMouseWheel += OnWheel;
        _scroll.PreviewMouseLeftButtonDown += OnDown;
        _scroll.PreviewMouseMove += OnMove;
        _scroll.PreviewMouseLeftButtonUp += (_, _) => EndDrag();
        _scroll.LostMouseCapture += (_, _) => _dragFrom = null;
    }

    public ImageSource? Source { get => (ImageSource?)GetValue(SourceProperty); set => SetValue(SourceProperty, value); }

    /// <summary>1 is 100% of the decoded image. 0 fits the whole image in the view.</summary>
    public double Zoom { get => (double)GetValue(ZoomProperty); set => SetValue(ZoomProperty, value); }

    void Relayout()
    {
        _image.Source = Source;
        if (Source is null || ActualWidth <= 0) return;
        var width = Source.Width;
        var height = Source.Height;
        var scale = Zoom > 0 ? Zoom : Math.Min(1, Math.Min((ActualWidth - 24) / width, (ActualHeight - 24) / height));
        _image.Width = Math.Max(1, width * scale);
        _image.Height = Math.Max(1, height * scale);
    }

    double CurrentScale => Source is { Width: > 0 } source ? _image.Width / source.Width : 1;

    void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || Source is null) return;
        e.Handled = true;
        var current = CurrentScale;
        Zoom = e.Delta > 0
            ? ZoomSteps.FirstOrDefault(z => z > current + 0.01, ZoomSteps[^1])
            : ZoomSteps.LastOrDefault(z => z < current - 0.01, ZoomSteps[0]);
    }

    void OnDown(object sender, MouseButtonEventArgs e)
    {
        _scroll.Focus();
        _dragFrom = e.GetPosition(_scroll);
        _dragOffset = new Point(_scroll.HorizontalOffset, _scroll.VerticalOffset);
        _scroll.CaptureMouse();
        e.Handled = true;
    }

    void OnMove(object sender, MouseEventArgs e)
    {
        if (_dragFrom is not { } from) return;
        var now = e.GetPosition(_scroll);
        _scroll.ScrollToHorizontalOffset(_dragOffset.X - (now.X - from.X));
        _scroll.ScrollToVerticalOffset(_dragOffset.Y - (now.Y - from.Y));
    }

    void EndDrag()
    {
        _dragFrom = null;
        if (_scroll.IsMouseCaptured) _scroll.ReleaseMouseCapture();
    }
}
