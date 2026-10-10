using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Bibliotaph.App.Controls;
using Bibliotaph.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace Bibliotaph.App.Services;

/// <summary>
/// Slice 4k: accessibility audits of what the smoke test shows, page by page and dialog by dialog. Each audit checks
/// that every interactive control has a name a screen reader can read; then, with the window at its minimum size (what
/// a small screen at 200% scaling leaves), that no primary control is cut off and that Tab reaches every one and comes
/// back round. Misses are gathered rather than thrown, so the checks around them still run, and one check at the end
/// reports them all.
/// </summary>
/// <remarks>
/// A primary control is a visible button, toggle, check box, radio button, drop-down or text box that isn't part of
/// another control (a drop-down's arrow, a scroll bar's buttons, a text box's insides) and isn't in a popup. One is cut
/// off when part of it lies outside the window, or outside something that clips it, unless that something is a scroll
/// viewer that can scroll it into view on that side.
/// </remarks>
static partial class SmokeTest
{
    /// <summary>Layout rounding can leave a control a fraction of a pixel over its parent's edge.</summary>
    const double ClipTolerance = 1;

    static readonly List<string> AccessibilityMisses = [];
    static readonly HashSet<string> Audited = [];
    static int _tabWalksSkipped;
    static long _auditTicks;

    /// <summary>
    /// Audits what <paramref name="window"/> shows now, as <paramref name="where"/>: once per name, so a scenario run in
    /// both themes is audited in the first. An in-page dialog, when one is open, is where Tab is walked, as it keeps
    /// focus in itself. The window gets its size back afterwards, and focus goes back where it was.
    /// </summary>
    static async Task AuditAsync(Window window, string where)
    {
        if (!Audited.Add(where)) return;
        var started = Stopwatch.GetTimestamp();
        var (state, width, height) = (window.WindowState, window.Width, window.Height);
        var shrink = window.ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip && window.MinWidth > 0 && window.MinHeight > 0;
        try
        {
            CheckNames(window, where);
            if (shrink)
            {
                window.WindowState = WindowState.Normal;
                window.Width = window.MinWidth;
                window.Height = window.MinHeight;
                await Settle(window);
            }
            CheckClipping(window, where);
            await WalkTabsAsync(window, where);
        }
        catch (Exception ex)
        {
            // The audit itself failing is a miss too, but not one that stops the scenario it is in.
            AccessibilityMisses.Add($"{where}: the audit failed: {ex.Message}");
            Log.Error(ex, "Smoke test: the accessibility audit of {Where} failed", where);
        }
        finally
        {
            if (shrink)
            {
                window.Width = width;
                window.Height = height;
                window.WindowState = state;
                await Settle(window);
            }
            _auditTicks += Stopwatch.GetTimestamp() - started;
        }
    }

    /// <summary>The check at the end of the run: every miss the audits found, in one failure.</summary>
    static Task ReportAccessibilityAsync()
    {
        var seconds = (double)_auditTicks / Stopwatch.Frequency;
        Log.Information("Smoke test: {Count} accessibility audits took {Seconds:F1} s in all", Audited.Count, seconds);
        if (_tabWalksSkipped > 0)
            Log.Warning("Smoke test: Tab order went unchecked in {Count} audits, as the window didn't get keyboard focus", _tabWalksSkipped);
        if (AccessibilityMisses.Count == 0) return Task.CompletedTask;
        foreach (var miss in AccessibilityMisses) Log.Error("Smoke test accessibility miss: {Miss}", miss);
        throw new InvalidOperationException($"{AccessibilityMisses.Count} accessibility misses: {string.Join(" | ", AccessibilityMisses)}");
    }

    static void Miss(string where, string what) => AccessibilityMisses.Add($"{where}: {what}");

    // ---- Names --------------------------------------------------------------------------------------------------

    /// <summary>Every interactive control showing has a name, and not its view model's type name read out by default.</summary>
    static void CheckNames(Window window, string where)
    {
        foreach (var element in Showing(window))
        {
            if (!IsInteractive(element) || IsInsideAnother(element, window)) continue;
            var name = AccessibleName(element);
            if (string.IsNullOrWhiteSpace(name)) Miss(where, $"{Describe(element, window)} has no automation name");
            else if (ReadsAsTypeName(element, name)) Miss(where, $"{Describe(element, window)} is read as \"{name}\", a type name");
        }
    }

    /// <summary>
    /// Buttons and menu items count even when they don't take focus (a mouse or a screen reader can still press them);
    /// other controls when they take focus, which is what makes them interactive.
    /// </summary>
    static bool IsInteractive(FrameworkElement element) => element switch
    {
        ButtonBase or MenuItem => true,
        ComboBox or TextBoxBase or PasswordBox or ListBox or ListBoxItem or Slider => element.Focusable,
        _ => element.Focusable && KeyboardNavigation.GetIsTabStop(element) && element is not Control { IsTabStop: false },
    };

    /// <summary>The parts of a drop-down, text box, password box, slider or scroll bar belong to it, and screen readers only meet it.</summary>
    static bool IsInsideAnother(DependencyObject element, Window window)
    {
        for (var node = VisualTreeHelper.GetParent(element); node is not null && node != window; node = VisualTreeHelper.GetParent(node))
            if (node is ComboBox or TextBoxBase or PasswordBox or ScrollBar or Slider) return true;
        return false;
    }

    /// <summary>What UI Automation reads for the element: its automation name, or what its peer makes of its content.</summary>
    static string AccessibleName(FrameworkElement element)
    {
        var peer = UIElementAutomationPeer.CreatePeerForElement(element);
        return peer?.GetName() is { Length: > 0 } name ? name : AutomationProperties.GetName(element);
    }

    /// <summary>A view model with no name of its own is read as its ToString: its type's name, or a record's.</summary>
    static bool ReadsAsTypeName(FrameworkElement element, string name)
    {
        if (name.StartsWith("Bibliotaph.", StringComparison.Ordinal) || name.StartsWith("System.", StringComparison.Ordinal)) return true;
        foreach (var source in new[] { element.DataContext, (element as ContentControl)?.Content })
        {
            if (source is null or string or UIElement) continue;
            var type = source.GetType();
            if (name == type.FullName || name == type.Name || name.StartsWith(type.Name + " {", StringComparison.Ordinal)) return true;
        }
        return false;
    }

    // ---- Clipping -------------------------------------------------------------------------------------------------

    /// <summary>At the window's present (minimum) size, no primary control is cut off by the window or a parent.</summary>
    static void CheckClipping(Window window, string where)
    {
        if (VisualTreeHelper.GetChildrenCount(window) == 0 || VisualTreeHelper.GetChild(window, 0) is not FrameworkElement client) return;
        foreach (var element in Showing(window))
        {
            if (element is not (ButtonBase or ComboBox or TextBoxBase or PasswordBox) || IsInsideAnother(element, window)) continue;
            if (CutOffBy(element, client) is { } by) Miss(where, $"{Describe(element, window)} is cut off by {by} at {window.ActualWidth:F0}×{window.ActualHeight:F0}");
        }
    }

    /// <summary>
    /// What cuts <paramref name="element"/> off, if anything: its own layout (a slot smaller than it needs), a parent's
    /// clip, or the window's edge. Past a scroll viewer that can scroll on an axis, only the part in its viewport counts
    /// on that axis, and nothing at all if none of it is in view: scrolling brings it to view.
    /// </summary>
    static string? CutOffBy(FrameworkElement element, FrameworkElement client)
    {
        if (element.RenderSize.Width <= 0 || element.RenderSize.Height <= 0) return "its parent, which leaves it no room";
        Visual at = element;
        var rect = new Rect(element.RenderSize);
        for (var node = (DependencyObject)element; node is not null && node != client; node = VisualTreeHelper.GetParent(node))
        {
            if (node is not Visual visual) continue;
            if (!ReferenceEquals(visual, at))
            {
                rect = at.TransformToAncestor(visual).TransformBounds(rect);
                at = visual;
            }
            if (VisualTreeHelper.GetClip(visual) is not { } clip) continue;
            var area = clip.Bounds;
            // A list's presenter hands scrolling to its panel, so the scroll viewer is found through its template.
            var scroller = visual is ScrollContentPresenter presenter ? presenter.TemplatedParent as ScrollViewer ?? presenter.ScrollOwner : null;
            var outX = rect.Left < area.Left - ClipTolerance || rect.Right > area.Right + ClipTolerance;
            var outY = rect.Top < area.Top - ClipTolerance || rect.Bottom > area.Bottom + ClipTolerance;
            if (scroller is { ScrollableWidth: > 0 })
            {
                if (rect.Right <= area.Left || rect.Left >= area.Right) return null;
                rect = new Rect(Math.Max(rect.Left, area.Left), rect.Top, Math.Min(rect.Right, area.Right) - Math.Max(rect.Left, area.Left), rect.Height);
                outX = false;
            }
            if (scroller is { ScrollableHeight: > 0 })
            {
                if (rect.Bottom <= area.Top || rect.Top >= area.Bottom) return null;
                rect = new Rect(rect.Left, Math.Max(rect.Top, area.Top), rect.Width, Math.Min(rect.Bottom, area.Bottom) - Math.Max(rect.Top, area.Top));
                outY = false;
            }
            if (outX || outY) return ReferenceEquals(visual, element) ? "its own slot, smaller than it needs" : Describe((DependencyObject)visual, null);
        }
        rect = at.TransformToAncestor(client).TransformBounds(rect);
        var edge = new Rect(client.RenderSize);
        if (rect.Left < -ClipTolerance || rect.Top < -ClipTolerance || rect.Right > edge.Right + ClipTolerance || rect.Bottom > edge.Bottom + ClipTolerance)
            return "the window's edge";
        return null;
    }

    // ---- Tab order ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Tab, as the keyboard moves focus, from the first control of the window (or of the in-page dialog that holds
    /// focus) until it comes back round. It must reach every primary control that takes focus, never stop on one with
    /// no name or on an empty container, and never stick. A list that Tab enters once counts as reaching its items, and
    /// a group of radio buttons counts as reached through any one of them.
    /// </summary>
    static async Task WalkTabsAsync(Window window, string where)
    {
        var scope = (FrameworkElement?)Showing(window).LastOrDefault(e => e is UserControl && KeyboardNavigation.GetTabNavigation(e) == KeyboardNavigationMode.Cycle)
            ?? window;
        var expected = Showing(scope).Where(e => IsTabStop(e, scope, window)).ToList();
        if (expected.Count == 0) return;

        var before = Keyboard.FocusedElement;
        window.Activate();
        if (!scope.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)) || Keyboard.FocusedElement is not DependencyObject first
            || !IsWithin(first, scope))
        {
            // A window that isn't in the foreground may not get keyboard focus; the names and clipping still count.
            _tabWalksSkipped++;
            Log.Warning("Smoke test: {Where} didn't get keyboard focus, so its Tab order isn't checked", where);
            return;
        }

        var reached = new HashSet<DependencyObject> { first };
        var current = first;
        var limit = expected.Count * 3 + 60;
        string? broken = null;
        for (var presses = 0; ; presses++)
        {
            if (presses == limit)
            {
                broken = $"Tab didn't come back round to {Describe(first, window)} within {limit} presses";
                break;
            }
            // A link in text (the breadcrumb) is a content element, not a UI element, and moves focus on its own.
            var request = new TraversalRequest(FocusNavigationDirection.Next);
            var moved = current switch
            {
                UIElement from => from.MoveFocus(request),
                ContentElement from => from.MoveFocus(request),
                _ => false,
            };
            if (!moved || Keyboard.FocusedElement is not DependencyObject next || ReferenceEquals(next, current))
            {
                broken = $"Tab stuck at {Describe(current, window)}";
                break;
            }
            if (ReferenceEquals(next, first)) break;
            if (!IsWithin(next, scope))
            {
                broken = $"Tab left {Describe(scope, window)} from {Describe(current, window)} for {Describe(next, window)}";
                break;
            }
            reached.Add(next);
            current = next;
        }
        if (broken is not null) Miss(where, broken);

        foreach (var stop in reached.OfType<FrameworkElement>())
        {
            if (stop.GetType() == typeof(ItemsControl) || stop.GetType() == typeof(ContentControl))
                Miss(where, $"Tab stops on {Describe(stop, window)}, which does nothing");
            else if (!IsInteractive(stop) || IsInsideAnother(stop, window))
            {
                if (string.IsNullOrWhiteSpace(AccessibleName(stop))) Miss(where, $"Tab stops on {Describe(stop, window)}, which has no automation name");
            }
        }
        foreach (var control in expected)
            if (!Reached(control, reached, scope)) Miss(where, $"Tab never reaches {Describe(control, window)}");

        // Focus goes back where it was. A text box isn't given it back: the search box opens its guide as focus arrives.
        if (before is UIElement { IsVisible: true, Focusable: true } and not TextBoxBase) ((UIElement)before).Focus();
        else Keyboard.ClearFocus();
        await Settle(window);
    }

    /// <summary>A primary control that Tab is meant to stop on.</summary>
    static bool IsTabStop(FrameworkElement element, FrameworkElement scope, Window window)
    {
        if (element is not (ButtonBase or ComboBox or TextBoxBase or PasswordBox or ListBox or Slider)
            || element is not Control { Focusable: true, IsEnabled: true, IsTabStop: true } || !KeyboardNavigation.GetIsTabStop(element)
            || IsInsideAnother(element, window))
            return false;
        // Inside a container Tab skips (as a text box skips its insides), it isn't meant to be reached.
        for (var node = VisualTreeHelper.GetParent(element); node is not null && node != scope; node = VisualTreeHelper.GetParent(node))
            if (KeyboardNavigation.GetTabNavigation(node) == KeyboardNavigationMode.None) return false;
        return true;
    }

    /// <summary>Whether Tab reached <paramref name="control"/>, its list (which Tab enters once), or another radio button in its group.</summary>
    static bool Reached(FrameworkElement control, HashSet<DependencyObject> reached, FrameworkElement scope)
    {
        if (reached.Contains(control)) return true;
        for (var node = VisualTreeHelper.GetParent(control); node is not null && node != scope; node = VisualTreeHelper.GetParent(node))
            if (KeyboardNavigation.GetTabNavigation(node) == KeyboardNavigationMode.Once && reached.Any(r => r == node || IsWithin(r, node))) return true;
        if (control is RadioButton radio)
            return reached.OfType<RadioButton>().Any(other => string.IsNullOrEmpty(radio.GroupName)
                ? ReferenceEquals(VisualTreeHelper.GetParent(other), VisualTreeHelper.GetParent(radio))
                : other.GroupName == radio.GroupName);
        return false;
    }

    static bool IsWithin(DependencyObject element, DependencyObject ancestor)
    {
        for (var node = (DependencyObject?)element; node is not null; node = Parent(node))
            if (ReferenceEquals(node, ancestor)) return true;
        return false;
    }

    /// <summary>The visual parent, or for a content element such as a link in text, the element that holds it.</summary>
    static DependencyObject? Parent(DependencyObject node) => node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);

    // ---- Shared ---------------------------------------------------------------------------------------------------

    /// <summary>Every element of <paramref name="root"/> that is showing, in tree order; a hidden one's children are skipped.</summary>
    static List<FrameworkElement> Showing(DependencyObject root)
    {
        var found = new List<FrameworkElement>();
        var stack = new Stack<DependencyObject>();
        for (var i = VisualTreeHelper.GetChildrenCount(root) - 1; i >= 0; i--) stack.Push(VisualTreeHelper.GetChild(root, i));
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node is UIElement { IsVisible: false }) continue;
            if (node is FrameworkElement element) found.Add(element);
            for (var i = VisualTreeHelper.GetChildrenCount(node) - 1; i >= 0; i--) stack.Push(VisualTreeHelper.GetChild(node, i));
        }
        return found;
    }

    /// <summary>
    /// A control as a person could find it: its type, its x:Name or what it reads, and the named parts and views it
    /// sits in, innermost last.
    /// </summary>
    static string Describe(DependencyObject? element, Window? window)
    {
        if (element is null) return "nothing";
        static string Label(DependencyObject node) => node switch
        {
            FrameworkElement { Name.Length: > 0 } named => $"{node.GetType().Name} {named.Name}",
            ContentControl { Content: string text } => $"{node.GetType().Name} \"{text}\"",
            FrameworkElement { ToolTip: string tip } => $"{node.GetType().Name} \"{tip}\"",
            FrameworkElement framework when AutomationProperties.GetName(framework) is { Length: > 0 } name => $"{node.GetType().Name} \"{name}\"",
            _ => node.GetType().Name,
        };
        var path = new List<string>();
        for (var node = Parent(element); node is not null && node != window && path.Count < 4; node = Parent(node))
            if (node is UserControl or Window or FrameworkElement { Name.Length: > 0 } and not ContentPresenter) path.Add(Label(node));
        path.Reverse();
        return path.Count == 0 ? Label(element) : $"{Label(element)} in {string.Join(" > ", path)}";
    }

    // ---- Scenarios ------------------------------------------------------------------------------------------------

    /// <summary>
    /// The Windows contrast palette, applied as a contrast theme would apply it: it defines every key the light and dark
    /// palettes do, its brushes are the system's colours, and every route draws in it without a binding error (which
    /// fails the run at the end). The palette is put back to follow Windows afterwards.
    /// </summary>
    static async Task ShowContrastAsync(IServiceProvider services, Window window)
    {
        static HashSet<string> Keys(string file) =>
            [.. new ResourceDictionary { Source = new Uri($"pack://application:,,,/Themes/{file}") }.Keys.OfType<string>()];
        var light = Keys("Tokens.Light.xaml");
        var contrast = Keys("Tokens.Contrast.xaml");
        if (!light.SetEquals(Keys("Tokens.Dark.xaml")) || !light.SetEquals(contrast))
            throw new InvalidOperationException($"The palettes' keys differ: the contrast palette lacks {string.Join(", ", light.Except(contrast))} and adds {string.Join(", ", contrast.Except(light))}.");

        var theme = services.GetRequiredService<ThemeService>();
        var navigation = services.GetRequiredService<INavigationService>();
        theme.UseHighContrastForTest(true);
        try
        {
            await Settle(window);
            if (!theme.IsHighContrast || window.FindResource("Bt.Bg") is not SolidColorBrush { } background || background.Color != SystemColors.WindowColor
                || window.FindResource("Bt.Accent") is not SolidColorBrush { } accent || accent.Color != SystemColors.HighlightColor)
                throw new InvalidOperationException("The contrast palette isn't the system's window and highlight colours.");
            foreach (var route in Enum.GetValues<Route>())
            {
                navigation.NavigateTo(route);
                await Settle(window);
                if (navigation.Current is SettingsViewModel settings) await ShowEachSectionAsync(window, settings);
            }
            while (navigation.GoBack()) await Settle(window);
        }
        finally
        {
            theme.UseHighContrastForTest(null);
            await Settle(window);
        }
        if (theme.IsHighContrast != SystemParameters.HighContrast) throw new InvalidOperationException("The palette didn't go back to following Windows.");
    }

    /// <summary>
    /// The Keyboard shortcuts popup: Ctrl+/'s command opens it over the main window, listing every area of the app, and
    /// Close closes it; the link at the foot of the Settings sections opens it too, and Esc's Close closes it again.
    /// </summary>
    static async Task ShowShortcutsAsync(IServiceProvider services, Window window)
    {
        if (KeyboardShortcuts.All.Count == 0 || KeyboardShortcuts.All.Any(a => a.Shortcuts.Count == 0))
            throw new InvalidOperationException("The keyboard shortcuts list has an empty area.");
        var gesture = window.InputBindings.OfType<KeyBinding>().FirstOrDefault(b => b.Command == ShellCommands.ShowShortcuts);
        if (gesture is not { Key: Key.OemQuestion, Modifiers: ModifierKeys.Control }) throw new InvalidOperationException("Ctrl+/ doesn't open the keyboard shortcuts.");

        // ShowDialog returns only when the popup closes, so run the command from the queue and go on inside the popup's loop.
        _ = window.Dispatcher.BeginInvoke(() => ShellCommands.ShowShortcuts.Execute(null, window));
        var dialog = await OpenedAsync<Views.ShortcutsDialog>(window, "Ctrl+/");
        var areas = Descendants<TextBlock>(dialog).Where(t => t.IsVisible).Select(t => t.Text).ToHashSet();
        if (KeyboardShortcuts.All.FirstOrDefault(a => !areas.Contains(a.Name)) is { } missing)
            throw new InvalidOperationException($"The keyboard shortcuts popup doesn't show {missing.Name}.");
        await AuditAsync(dialog, "the Keyboard shortcuts popup");
        Click(dialog.FindName("CloseShortcuts") as Button, "Close");
        await ClosedAsync<Views.ShortcutsDialog>(window, "Close");

        var navigation = services.GetRequiredService<INavigationService>();
        navigation.NavigateTo(Route.Settings);
        await Settle(window);
        var link = Clickable(Descendants<Button>(window).FirstOrDefault(b => b.Name == "ShowShortcuts"), "Keyboard shortcuts");
        _ = window.Dispatcher.BeginInvoke(link.Invoke);
        dialog = await OpenedAsync<Views.ShortcutsDialog>(window, "Settings' Keyboard shortcuts link");
        // Esc presses the Close button, as IsCancel makes it.
        Click(dialog.FindName("CloseShortcuts") as Button is { IsCancel: true } close ? close : null, "Close (Esc)");
        await ClosedAsync<Views.ShortcutsDialog>(window, "Esc");
        navigation.GoBack();
        await Settle(window);
    }

    static async Task<T> OpenedAsync<T>(Window window, string how) where T : Window
    {
        T? opened = null;
        await WaitUntilAsync(window, () => (opened = Application.Current.Windows.OfType<T>().FirstOrDefault()) is { IsLoaded: true, IsVisible: true },
            () => $"{how} didn't open the {typeof(T).Name}.");
        await Settle(opened!);
        return opened!;
    }

    static Task ClosedAsync<T>(Window window, string how) where T : Window =>
        WaitUntilAsync(window, () => !Application.Current.Windows.OfType<T>().Any(), () => $"{how} didn't close the {typeof(T).Name}.");
}
