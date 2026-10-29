using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Bibliotaph.App.Controls;
using Bibliotaph.App.Services;
using Bibliotaph.App.ViewModels;
using Bibliotaph.Core;

namespace Bibliotaph.App.Views;

/// <summary>
/// The tour's overlay: the window dimmed but for the step's controls, found by <see cref="Ui.TourAnchorProperty"/>,
/// with the callout beside them. It follows them as the window is resized or the sidebar folded, and passes over a
/// step whose controls aren't on screen, as Home's covers aren't from the Library.
/// </summary>
public partial class TourView
{
    const double Pad = 6, Gap = 14, Edge = 12;
    /// <summary>How long a step's controls get to appear after a navigation before the step is passed over.</summary>
    static readonly TimeSpan Patience = TimeSpan.FromSeconds(2);

    /// <summary>A row of covers is as much of the Library's grid as the spotlight takes in.</summary>
    const double RowHeight = 270;

    readonly DispatcherTimer _retry = new() { Interval = TimeSpan.FromMilliseconds(100) };
    /// <summary>The step's controls once found, so a layout pass only measures them again.</summary>
    readonly List<FrameworkElement> _targets = [];
    TourViewModel? _tour;
    Rect _spot = Rect.Empty;
    DateTime _lookingSince;

    public TourView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        IsVisibleChanged += (_, _) => { if (IsVisible) Place(); };
        LayoutUpdated += (_, _) => { if (IsVisible && !_retry.IsEnabled) Place(); };
        _retry.Tick += (_, _) => Place();
    }

    void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_tour is not null) _tour.StepChanged -= OnStepChanged;
        _tour = e.NewValue as TourViewModel;
        if (_tour is not null) _tour.StepChanged += OnStepChanged;
    }

    void OnStepChanged(object? sender, EventArgs e)
    {
        _spot = Rect.Empty;
        _targets.Clear();
        _lookingSince = DateTime.UtcNow;
        // The page's controls may still be loading, so keep looking for a moment.
        _retry.Start();
        Place();
    }

    /// <summary>Draws the spotlight round the step's controls and the callout beside them, once they are laid out.</summary>
    void Place()
    {
        if (_tour?.Current is not { } step || Window.GetWindow(this) is not { } window || ActualWidth == 0) return;
        if (_targets.Count == 0 || _targets.Any(t => !t.IsVisible || !t.IsDescendantOf(window)))
        {
            _targets.Clear();
            _targets.AddRange(Targets(window, step.Anchors));
            // A row of covers below the fold scrolls up first; the next layout pass places the spotlight.
            if (_targets.Count > 0) _targets[0].BringIntoView();
        }
        var spot = Rect.Empty;
        foreach (var element in _targets)
        {
            var size = element.RenderSize;
            if (step.Anchors.Contains(TourAnchor.FirstBooks)) size.Height = Math.Min(size.Height, RowHeight);
            Rect bounds;
            try
            {
                // The controls are beside this overlay in the window's tree, not under it, so the transform goes by way of the window.
                bounds = element.TransformToVisual(this).TransformBounds(new Rect(size));
            }
            catch (InvalidOperationException)
            {
                // Taken out of the tree between the walk and the measure: found again next time.
                _targets.Clear();
                return;
            }
            spot = spot.IsEmpty ? bounds : Rect.Union(spot, bounds);
        }
        if (spot.IsEmpty)
        {
            if (DateTime.UtcNow - _lookingSince < Patience) return;
            _retry.Stop();
            _tour.PassOverAsync().FireAndForget();
            return;
        }
        var focusNext = _retry.IsEnabled;
        _retry.Stop();
        spot.Inflate(Pad, Pad);
        if (spot == _spot) return;
        _spot = spot;
        Backdrop.Data = new CombinedGeometry(GeometryCombineMode.Exclude,
            new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight)),
            new RectangleGeometry(spot, 8, 8));
        Callout.Measure(new Size(ActualWidth, ActualHeight));
        var (left, top) = Beside(spot, Callout.DesiredSize);
        Canvas.SetLeft(Callout, left);
        Canvas.SetTop(Callout, top);
        if (focusNext) TourNext.Focus();
    }

    /// <summary>To the right of the spotlight when there's room, else below, else above, kept inside the window.</summary>
    (double Left, double Top) Beside(Rect spot, Size size)
    {
        double left, top;
        if (spot.Right + Gap + size.Width + Edge <= ActualWidth)
        {
            left = spot.Right + Gap;
            top = spot.Top - 8;
        }
        else if (spot.Bottom + Gap + size.Height + Edge <= ActualHeight)
        {
            left = spot.Right - size.Width;
            top = spot.Bottom + Gap;
        }
        else
        {
            left = spot.Right - size.Width;
            top = spot.Top - Gap - size.Height;
        }
        left = Math.Clamp(left, Edge, Math.Max(Edge, ActualWidth - size.Width - Edge));
        top = Math.Clamp(top, Edge, Math.Max(Edge, ActualHeight - size.Height - Edge));
        return (left, top);
    }

    static IEnumerable<FrameworkElement> Targets(DependencyObject parent, IReadOnlyList<TourAnchor> anchors)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is FrameworkElement { IsVisible: true } element && anchors.Contains(Ui.GetTourAnchor(element)) && element.ActualWidth > 0)
            {
                yield return element;
                continue;
            }
            if (child is UIElement { IsVisible: false }) continue;
            foreach (var deeper in Targets(child, anchors)) yield return deeper;
        }
    }

    // The arrow keys step through the tour; Esc skips it from the input binding.
    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_tour is null || Keyboard.Modifiers != ModifierKeys.None) return;
        if (e.Key == Key.Right && _tour.NextCommand.CanExecute(null)) _tour.NextCommand.Execute(null);
        else if (e.Key == Key.Left && _tour.BackCommand.CanExecute(null)) _tour.BackCommand.Execute(null);
        else return;
        e.Handled = true;
    }
}
