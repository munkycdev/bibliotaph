using AppKit;
using Bibliotaph.Mac.Binding;
using Bibliotaph.Presentation.ViewModels;
using CoreGraphics;
using Foundation;
using ObjCRuntime;

namespace Bibliotaph.Mac.Views;

/// <summary>
/// The search guide under the toolbar's search field, as Windows has it under its box: the fields that can be searched,
/// and after a field's colon the values in the library with how many books have each. It opens when the empty field
/// gets the keyboard, follows the word being typed, and opens anywhere with ⌥Esc, the Mac's key for completions. ↓ and
/// ↑ move through it, Return or Tab picks, Esc shuts it; a click picks too. The keyboard stays in the field throughout.
/// </summary>
sealed class SearchGuidePanel : IDisposable
{
    readonly SearchGuideViewModel _guide;
    readonly NSSearchField _field;
    readonly Func<bool> _asking;
    readonly Action<string> _typed;
    readonly GuideWindow _panel;
    readonly NSStackView _rows;
    readonly NSTextField _heading;
    readonly NSScrollView _scroll;
    readonly Bindings _bindings = new();
    readonly List<(GuideSuggestionViewModel Row, RowView View)> _shown = [];
    bool _applying;

    const float Width = 440;

    public SearchGuidePanel(SearchGuideViewModel guide, NSSearchField field, Func<bool> asking, Action<string> typed)
    {
        _guide = guide;
        _field = field;
        _asking = asking;
        _typed = typed;
        _heading = NSTextField.CreateLabel("");
        _heading.Font = NSFont.SystemFontOfSize(11, NSFontWeight.Semibold)!;
        _heading.TextColor = NSColor.SecondaryLabel;
        _rows = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Vertical,
            Alignment = NSLayoutAttribute.Leading,
            Spacing = 0,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        var document = new FlippedView { TranslatesAutoresizingMaskIntoConstraints = false };
        document.AddSubview(_rows);
        _scroll = new NSScrollView { HasVerticalScroller = true, DrawsBackground = false, DocumentView = document, TranslatesAutoresizingMaskIntoConstraints = false };
        var effect = new NSVisualEffectView
        {
            Material = NSVisualEffectMaterial.Menu,
            BlendingMode = NSVisualEffectBlendingMode.BehindWindow,
            State = NSVisualEffectState.Active,
            WantsLayer = true,
        };
        effect.Layer!.CornerRadius = 10;
        effect.Layer.MasksToBounds = true;
        _heading.TranslatesAutoresizingMaskIntoConstraints = false;
        effect.AddSubview(_heading);
        effect.AddSubview(_scroll);
        NSLayoutConstraint.ActivateConstraints(
        [
            _heading.TopAnchor.ConstraintEqualTo(effect.TopAnchor, 10),
            _heading.LeadingAnchor.ConstraintEqualTo(effect.LeadingAnchor, 14),
            _scroll.TopAnchor.ConstraintEqualTo(_heading.BottomAnchor, 6),
            _scroll.LeadingAnchor.ConstraintEqualTo(effect.LeadingAnchor),
            _scroll.TrailingAnchor.ConstraintEqualTo(effect.TrailingAnchor),
            _scroll.BottomAnchor.ConstraintEqualTo(effect.BottomAnchor, -6),
            _rows.TopAnchor.ConstraintEqualTo(document.TopAnchor),
            _rows.LeadingAnchor.ConstraintEqualTo(document.LeadingAnchor),
            _rows.TrailingAnchor.ConstraintEqualTo(document.TrailingAnchor),
            _rows.BottomAnchor.ConstraintEqualTo(document.BottomAnchor),
            document.WidthAnchor.ConstraintEqualTo(_scroll.ContentView.WidthAnchor),
        ]);
        _panel = new GuideWindow(new CGRect(0, 0, Width, 200))
        {
            ContentView = effect,
            HasShadow = true,
            BackgroundColor = NSColor.Clear,
            IsOpaque = false,
        };
        _panel.ReleaseWhenClosed(false);

        _bindings.Properties(guide, [nameof(SearchGuideViewModel.IsOpen), nameof(SearchGuideViewModel.Suggestions), nameof(SearchGuideViewModel.Hint),
            nameof(SearchGuideViewModel.Heading)], Show);
        _bindings.Property(guide, nameof(SearchGuideViewModel.Highlighted), Highlight);
        guide.Edited += OnEdited;
        _bindings.Add(() => guide.Edited -= OnEdited);
    }

    /// <summary>The field took the keyboard: an empty field shows every field there is to search.</summary>
    public void Focused()
    {
        if (!_asking()) _guide.Focused(_field.StringValue, Caret());
    }

    /// <summary>What was typed changed; a pick being put in doesn't count.</summary>
    public void Changed()
    {
        if (!_applying && !_asking()) _guide.TextChanged(_field.StringValue, Caret());
    }

    /// <summary>The field let the keyboard go: the guide goes too, unless the keyboard is only passing through.</summary>
    public void Ended() => NSApplication.SharedApplication.BeginInvokeOnMainThread(() =>
    {
        if (_field.CurrentEditor is null) _guide.Close();
    });

    /// <summary>The keys the guide takes from the field; the rest are the field's.</summary>
    public bool Command(Selector selector)
    {
        if (_asking()) return false;
        switch (selector.Name)
        {
            case "complete:":
                _guide.Request(_field.StringValue, Caret());
                return true;
            case "moveDown:" when !_guide.IsOpen:
                _guide.Request(_field.StringValue, Caret());
                return _guide.IsOpen;
            case "moveDown:":
                return _guide.Move(1);
            case "moveUp:":
                return _guide.Move(-1);
            case "insertNewline:" or "insertTab:":
                return _guide.PickHighlighted();
            case "cancelOperation:" when _guide.IsOpen:
                _guide.Close();
                return true;
            case "moveLeft:" or "moveRight:" or "moveToBeginningOfLine:" or "moveToEndOfLine:":
                // After the field has moved the cursor.
                NSApplication.SharedApplication.BeginInvokeOnMainThread(() => _guide.CaretMoved(_field.StringValue, Caret()));
                return false;
            default:
                return false;
        }
    }

    public void Close() => _guide.Close();

    int Caret() => _field.CurrentEditor is NSTextView editor ? (int)editor.SelectedRange.Location : _field.StringValue.Length;

    void OnEdited(object? sender, Core.Search.GuideEdit edit) => Bindings.OnMain(() =>
    {
        _applying = true;
        try
        {
            _field.StringValue = edit.Text;
            _field.Window?.MakeFirstResponder(_field);
            if (_field.CurrentEditor is NSTextView editor) editor.SelectedRange = new NSRange(edit.Caret, 0);
            // The library searches as the box changes, as it does for typing.
            _typed(edit.Text);
        }
        finally
        {
            _applying = false;
        }
        _guide.TextChanged(_field.StringValue, Caret());
    });

    void Show()
    {
        if (!_guide.IsOpen || _field.Window is not { } host)
        {
            if (_panel.ParentWindow is { } parent) parent.RemoveChildWindow(_panel);
            _panel.OrderOut(null);
            return;
        }
        _heading.StringValue = _guide.Heading;
        foreach (var view in _rows.ArrangedSubviews) view.RemoveFromSuperview();
        _shown.Clear();
        if (_guide.Hint is { } hint) Add(new RowView(hint, null));
        foreach (var row in _guide.Suggestions)
        {
            var view = new RowView(row, () => _guide.PickCommand.Execute(row));
            _shown.Add((row, view));
            Add(view);
        }
        Highlight();

        var rowsHeight = _rows.FittingSize.Height;
        var height = (nfloat)Math.Min(rowsHeight, 320) + 40;
        var field = host.ConvertRectToScreen(_field.ConvertRectToView(_field.Bounds, null));
        var width = (nfloat)Math.Max(Width, field.Width);
        // Under the field, but kept on the screen when the field is near its trailing edge.
        var x = field.X;
        if (host.Screen?.VisibleFrame is { } screen) x = (nfloat)Math.Max(screen.X, Math.Min(x, screen.Right - width));
        var frame = new CGRect(x, field.Y - height - 6, width, height);
        _panel.SetFrame(frame, true);
        if (_panel.ParentWindow is null) host.AddChildWindow(_panel, NSWindowOrderingMode.Above);
        _panel.OrderFront(null);
        _scroll.ContentView.ScrollToPoint(CGPoint.Empty);
    }

    void Add(RowView view)
    {
        _rows.AddArrangedSubview(view);
        view.WidthAnchor.ConstraintEqualTo(_rows.WidthAnchor).Active = true;
    }

    void Highlight()
    {
        foreach (var (row, view) in _shown)
        {
            view.Highlighted = ReferenceEquals(row, _guide.Highlighted);
            if (view.Highlighted) view.ScrollRectToVisible(view.Bounds);
        }
        if (_guide.Announcement.Length > 0)
            NSAccessibility.PostNotification(NSApplication.SharedApplication, new NSString("AXAnnouncementRequested"),
                NSDictionary.FromObjectAndKey(new NSString(_guide.Announcement), new NSString("AXAnnouncementKey")));
    }

    public void Dispose()
    {
        _bindings.Dispose();
        _panel.OrderOut(null);
        _panel.Dispose();
    }

    /// <summary>A panel that never takes the keyboard from the search field.</summary>
    sealed class GuideWindow(CGRect frame) : NSPanel(frame, NSWindowStyle.Borderless | NSWindowStyle.NonactivatingPanel, NSBackingStore.Buffered, false)
    {
        public override bool CanBecomeKeyWindow => false;

        public override bool CanBecomeMainWindow => false;
    }

    /// <summary>A field ("system:  the game system, e.g. system:5e") or a value ("Forbidden Lands  system:… 12").</summary>
    sealed class RowView : NSView
    {
        readonly Action? _pick;
        bool _highlighted;

        public RowView(GuideSuggestionViewModel row, Action? pick)
        {
            _pick = pick;
            WantsLayer = true;
            Layer!.CornerRadius = 5;
            NSView[] parts = row switch
            {
                FieldSuggestionViewModel field =>
                [
                    Text(field.Name, NSFont.MonospacedSystemFont(12, NSFontWeight.Semibold), NSColor.Label),
                    Text(field.Description + field.Example, NSFont.SystemFontOfSize(12), NSColor.SecondaryLabel),
                ],
                ValueSuggestionViewModel value =>
                [
                    Text(value.Label, NSFont.SystemFontOfSize(12, NSFontWeight.Medium), NSColor.Label),
                    Text(value.Search, NSFont.MonospacedSystemFont(11, NSFontWeight.Regular), NSColor.SecondaryLabel),
                    new NSView(),
                    Text(value.Count, NSFont.SystemFontOfSize(11), NSColor.SecondaryLabel),
                ],
                _ => [],
            };
            var line = NSStackView.FromViews(parts);
            line.Spacing = 10;
            line.EdgeInsets = new NSEdgeInsets(5, 14, 5, 14);
            line.TranslatesAutoresizingMaskIntoConstraints = false;
            if (parts.Length > 1) parts[1].SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
            AddSubview(line);
            NSLayoutConstraint.ActivateConstraints(
            [
                line.TopAnchor.ConstraintEqualTo(TopAnchor),
                line.BottomAnchor.ConstraintEqualTo(BottomAnchor),
                line.LeadingAnchor.ConstraintEqualTo(LeadingAnchor, 6),
                line.TrailingAnchor.ConstraintEqualTo(TrailingAnchor, -6),
            ]);
            _spoken = row.Spoken;
        }

        readonly string _spoken;

        public override bool AccessibilityElement { get => true; set { } }

        public override string AccessibilityLabel => _spoken;

        public override string AccessibilityRole => NSAccessibilityRoles.ButtonRole;

        public bool Highlighted
        {
            get => _highlighted;
            set
            {
                _highlighted = value;
                Layer!.BackgroundColor = value ? NSColor.SelectedContentBackground.CGColor : null;
            }
        }

        public override void MouseDown(NSEvent theEvent) => _pick?.Invoke();

        public override void UpdateLayer() => Highlighted = _highlighted;

        static NSTextField Text(string text, NSFont? font, NSColor color)
        {
            var label = NSTextField.CreateLabel(text);
            label.Font = font!;
            label.TextColor = color;
            label.LineBreakMode = NSLineBreakMode.TruncatingTail;
            return label;
        }
    }

    sealed class FlippedView : NSView
    {
        public override bool IsFlipped => true;
    }
}

/// <summary>The toolbar's search field, which says when it takes the keyboard, for the guide to open.</summary>
sealed class GuideSearchField : NSSearchField
{
    public event EventHandler? Focused;

    public override bool BecomeFirstResponder()
    {
        var became = base.BecomeFirstResponder();
        if (became) NSApplication.SharedApplication.BeginInvokeOnMainThread(() => Focused?.Invoke(this, EventArgs.Empty));
        return became;
    }
}

/// <summary>The search field's typing, its losing the keyboard, and the keys the guide takes, passed to the guide.</summary>
sealed class GuideFieldDelegate(Action<string> typed, Func<SearchGuidePanel?> guide) : NSSearchFieldDelegate
{
    public override void Changed(NSNotification notification)
    {
        if (notification.Object is NSSearchField field) typed(field.StringValue);
        guide()?.Changed();
    }

    public override void EditingEnded(NSNotification notification) => guide()?.Ended();

    public override bool DoCommandBySelector(NSControl control, NSTextView textView, Selector commandSelector) =>
        guide()?.Command(commandSelector) == true;
}
