using AppKit;
using CoreGraphics;

namespace Bibliotaph.Mac.Views;

/// <summary>
/// A settings pane laid out as the Mac's own: rounded sections, each row its label on the leading side and its control
/// on the trailing side, with a short note under a section where one is needed. As SwiftUI's grouped form style.
/// </summary>
sealed class Form
{
    public Form()
    {
        Root = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Vertical,
            Alignment = NSLayoutAttribute.Leading,
            Spacing = 22,
            EdgeInsets = new NSEdgeInsets(22, 28, 28, 28),
        };
    }

    public NSStackView Root { get; }

    public FormSection Section(string? title = null, string? footer = null)
    {
        var section = new FormSection(title, footer);
        Add(section.View);
        return section;
    }

    /// <summary>A part of the pane outside any section, the pane's width.</summary>
    public void Add(NSView view)
    {
        Root.AddArrangedSubview(view);
        view.WidthAnchor.ConstraintEqualTo(Root.WidthAnchor, 1, -56).Active = true;
    }

    /// <summary>Text above the sections, for what the whole pane is about.</summary>
    public NSTextField Intro(string text)
    {
        var label = FormText.Note(text, 13);
        Add(label);
        return label;
    }

    /// <summary>Buttons under a section, at its trailing edge, as the Mac's settings put Add and the like.</summary>
    public NSStackView Buttons(params NSView[] buttons)
    {
        var row = NSStackView.FromViews([new NSView(), .. buttons]);
        row.Spacing = 8;
        row.Distribution = NSStackViewDistribution.Fill;
        Add(row);
        return row;
    }
}

/// <summary>A titled, rounded group of rows, with a note under it.</summary>
sealed class FormSection
{
    public FormSection(string? title, string? footer)
    {
        var box = new FormBox();
        Title = NSTextField.CreateLabel(title ?? "");
        Title.Font = NSFont.SystemFontOfSize(13, NSFontWeight.Semibold)!;
        Title.Hidden = title is null;
        Footer = FormText.Note(footer ?? "", 11);
        Footer.Hidden = string.IsNullOrEmpty(footer);
        View = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Vertical,
            Alignment = NSLayoutAttribute.Leading,
            Spacing = 7,
        };
        View.AddArrangedSubview(Title);
        View.AddArrangedSubview(box);
        View.AddArrangedSubview(Footer);
        box.WidthAnchor.ConstraintEqualTo(View.WidthAnchor).Active = true;
        Footer.WidthAnchor.ConstraintEqualTo(View.WidthAnchor, 1, -16).Active = true;
        View.SetCustomSpacing(4, box);
    }

    // The view holds the box, so the section needn't.
    FormBox Box => (FormBox)View.ArrangedSubviews[1];

    public NSStackView View { get; }

    public NSTextField Title { get; }

    public NSTextField Footer { get; }

    /// <summary>The note under the section; hidden while it has nothing to say.</summary>
    public void Note(string? text)
    {
        Footer.StringValue = text ?? "";
        Footer.Hidden = string.IsNullOrEmpty(text);
    }

    /// <summary>A row: its label, and under it a line of detail if given, on the leading side; the controls on the trailing side.</summary>
    public FormRow Row(string label, params NSView[] controls) => Box.Add(new FormRow(label, null, controls));

    public FormRow Row(string label, string? detail, params NSView[] controls) => Box.Add(new FormRow(label, detail, controls));

    /// <summary>A row whose content spans it, such as a list or a block of text.</summary>
    public FormRow Wide(NSView content) => Box.Add(new FormRow(content));

    /// <summary>Takes away every row, for a list shown again.</summary>
    public void Clear() => Box.Clear();
}

/// <summary>The section's rounded fill; each row but the first visible has a hairline above it.</summary>
sealed class FormBox : NSView
{
    readonly NSStackView _rows = new()
    {
        Orientation = NSUserInterfaceLayoutOrientation.Vertical,
        Alignment = NSLayoutAttribute.Leading,
        Spacing = 0,
        TranslatesAutoresizingMaskIntoConstraints = false,
    };

    public FormBox()
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        WantsLayer = true;
        Layer!.CornerRadius = 10;
        AddSubview(_rows);
        NSLayoutConstraint.ActivateConstraints(
        [
            _rows.TopAnchor.ConstraintEqualTo(TopAnchor),
            _rows.BottomAnchor.ConstraintEqualTo(BottomAnchor),
            _rows.LeadingAnchor.ConstraintEqualTo(LeadingAnchor),
            _rows.TrailingAnchor.ConstraintEqualTo(TrailingAnchor),
        ]);
    }

    public override bool WantsUpdateLayer => true;

    // In the appearance showing, so it follows light and dark.
    public override void UpdateLayer()
    {
        Layer!.BackgroundColor = NSColor.QuaternarySystemFill.CGColor;
        Layer.BorderColor = NSColor.Separator.CGColor;
        Layer.BorderWidth = 0.5f;
    }

    public FormRow Add(FormRow row)
    {
        _rows.AddArrangedSubview(row);
        row.WidthAnchor.ConstraintEqualTo(_rows.WidthAnchor).Active = true;
        NeedsLayout = true;
        return row;
    }

    public void Clear()
    {
        foreach (var view in _rows.ArrangedSubviews) view.RemoveFromSuperview();
    }

    public override void Layout()
    {
        var first = true;
        foreach (var row in _rows.ArrangedSubviews.OfType<FormRow>())
        {
            if (row.Hidden) continue;
            row.Separated = !first;
            first = false;
        }
        // A section with nothing showing in it isn't drawn.
        Hidden = first;
        base.Layout();
    }
}

/// <summary>A row of a section.</summary>
sealed class FormRow : NSView
{
    readonly NSView _line = new() { WantsLayer = true, TranslatesAutoresizingMaskIntoConstraints = false };
    readonly NSStackView? _text;

    public FormRow(string label, string? detail, NSView[] controls)
    {
        Label = NSTextField.CreateLabel(label);
        Label.Font = NSFont.SystemFontOfSize(13)!;
        Detail = FormText.Note(detail ?? "", 11);
        Detail.Hidden = string.IsNullOrEmpty(detail);
        Detail.PreferredMaxLayoutWidth = 360;
        _text = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Vertical,
            Alignment = NSLayoutAttribute.Leading,
            Spacing = 2,
            TranslatesAutoresizingMaskIntoConstraints = false,
        };
        var text = _text;
        text.AddArrangedSubview(Label);
        text.AddArrangedSubview(Detail);
        Controls = NSStackView.FromViews(controls);
        Controls.Spacing = 8;
        Controls.TranslatesAutoresizingMaskIntoConstraints = false;
        Controls.SetContentHuggingPriorityForOrientation(750, NSLayoutConstraintOrientation.Horizontal);
        Controls.SetContentCompressionResistancePriority(750, NSLayoutConstraintOrientation.Horizontal);
        text.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        Build(text, Controls);
    }

    public FormRow(NSView content)
    {
        Label = NSTextField.CreateLabel("");
        Detail = NSTextField.CreateLabel("");
        Controls = new NSStackView();
        TranslatesAutoresizingMaskIntoConstraints = false;
        content.TranslatesAutoresizingMaskIntoConstraints = false;
        AddSubview(content);
        AddLine();
        NSLayoutConstraint.ActivateConstraints(
        [
            content.TopAnchor.ConstraintEqualTo(TopAnchor, 10),
            content.BottomAnchor.ConstraintEqualTo(BottomAnchor, -10),
            content.LeadingAnchor.ConstraintEqualTo(LeadingAnchor, 12),
            content.TrailingAnchor.ConstraintEqualTo(TrailingAnchor, -12),
        ]);
    }

    public NSTextField Label { get; }

    public NSTextField Detail { get; }

    public NSStackView Controls { get; }

    public bool Separated
    {
        get => !_line.Hidden;
        set => _line.Hidden = !value;
    }

    /// <summary>A second line of detail under the first, such as a folder's path over how it stands.</summary>
    public NSTextField AddDetail()
    {
        var line = FormText.Note("", 11);
        line.PreferredMaxLayoutWidth = 360;
        _text?.AddArrangedSubview(line);
        return line;
    }

    /// <summary>The detail on one line, cut in the middle when it is too long, as a path is.</summary>
    public void OneLine()
    {
        Detail.MaximumNumberOfLines = 1;
        Detail.LineBreakMode = NSLineBreakMode.TruncatingMiddle;
        Detail.SetContentCompressionResistancePriority(100, NSLayoutConstraintOrientation.Horizontal);
    }

    public void ShowDetail(string? text)
    {
        Detail.StringValue = text ?? "";
        Detail.Hidden = string.IsNullOrEmpty(text);
    }

    void Build(NSView text, NSView controls)
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        AddSubview(text);
        AddSubview(controls);
        AddLine();
        NSLayoutConstraint.ActivateConstraints(
        [
            HeightAnchor.ConstraintGreaterThanOrEqualTo(38),
            text.LeadingAnchor.ConstraintEqualTo(LeadingAnchor, 12),
            text.CenterYAnchor.ConstraintEqualTo(CenterYAnchor),
            text.TopAnchor.ConstraintGreaterThanOrEqualTo(TopAnchor, 9),
            text.BottomAnchor.ConstraintLessThanOrEqualTo(BottomAnchor, -9),
            controls.TrailingAnchor.ConstraintEqualTo(TrailingAnchor, -12),
            controls.CenterYAnchor.ConstraintEqualTo(CenterYAnchor),
            controls.TopAnchor.ConstraintGreaterThanOrEqualTo(TopAnchor, 6),
            controls.BottomAnchor.ConstraintLessThanOrEqualTo(BottomAnchor, -6),
            controls.LeadingAnchor.ConstraintGreaterThanOrEqualTo(text.TrailingAnchor, 16),
        ]);
    }

    void AddLine()
    {
        AddSubview(_line);
        NSLayoutConstraint.ActivateConstraints(
        [
            _line.TopAnchor.ConstraintEqualTo(TopAnchor),
            _line.LeadingAnchor.ConstraintEqualTo(LeadingAnchor, 12),
            _line.TrailingAnchor.ConstraintEqualTo(TrailingAnchor, -12),
            _line.HeightAnchor.ConstraintEqualTo(1 / (NSScreen.MainScreen?.BackingScaleFactor ?? 2)),
        ]);
        _line.Hidden = true;
    }

    public override bool WantsUpdateLayer => true;

    public override void UpdateLayer() => _line.Layer!.BackgroundColor = NSColor.Separator.CGColor;
}

static class FormText
{
    /// <summary>Secondary text that wraps: a section's note, a row's detail.</summary>
    public static NSTextField Note(string text, nfloat size)
    {
        var label = NSTextField.CreateWrappingLabel(text);
        label.Font = NSFont.SystemFontOfSize(size)!;
        label.TextColor = NSColor.SecondaryLabel;
        return label;
    }

    /// <summary>A pop-up of choices, as the Mac's settings offer one of several.</summary>
    public static NSPopUpButton Choices(IEnumerable<string> titles, Action<int> chosen)
    {
        var popup = new NSPopUpButton(CGRect.Empty, false);
        foreach (var title in titles) popup.Menu!.AddItem(new NSMenuItem(title));
        popup.Activated += (_, _) => chosen((int)popup.IndexOfSelectedItem);
        return popup;
    }

    /// <summary>An on/off switch, as the Mac's settings turn a setting on or off.</summary>
    public static NSSwitch Switch(Action<bool> changed)
    {
        var toggle = new NSSwitch { ControlSize = NSControlSize.Small };
        toggle.Activated += (_, _) => changed(toggle.State == (nint)(long)NSCellStateValue.On);
        return toggle;
    }

    public static void Set(NSSwitch toggle, bool on) => toggle.State = on ? (nint)(long)NSCellStateValue.On : (nint)(long)NSCellStateValue.Off;
}
