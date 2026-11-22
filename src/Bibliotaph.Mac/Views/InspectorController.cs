using AppKit;
using Bibliotaph.Mac.Binding;
using Bibliotaph.Presentation.ViewModels;
using CoreGraphics;

namespace Bibliotaph.Mac.Views;

/// <summary>
/// The book's details beside the grid, as the Windows inspector shows them: its cover and title, then its metadata with
/// what Bibliotaph only suggests marked, what it knows from the file, where the file is, and how far reading it got.
/// Corrections are made on Windows for now; Reprocess and Show in Finder work here.
/// </summary>
public sealed class InspectorController(MacCovers covers) : NSViewController
{
    readonly Bindings _page = new();
    Bindings _shown = new();
    NSStackView _stack = null!;
    NSScrollView _scroll = null!;
    NSTextField _empty = null!;
    LibraryViewModel? _library;

    /// <summary>The inspector opened or closed, for the split view to show or fold its pane.</summary>
    public event EventHandler<bool>? Showing;

    public override void LoadView()
    {
        _stack = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Vertical,
            Alignment = NSLayoutAttribute.Leading,
            Spacing = 8,
            EdgeInsets = new NSEdgeInsets(16, 16, 20, 16),
        };
        var document = new FlippedView();
        _stack.TranslatesAutoresizingMaskIntoConstraints = false;
        document.AddSubview(_stack);
        _scroll = new NSScrollView { DocumentView = document, HasVerticalScroller = true, DrawsBackground = false };
        document.TranslatesAutoresizingMaskIntoConstraints = false;
        NSLayoutConstraint.ActivateConstraints(
        [
            _stack.TopAnchor.ConstraintEqualTo(document.TopAnchor),
            _stack.LeadingAnchor.ConstraintEqualTo(document.LeadingAnchor),
            _stack.TrailingAnchor.ConstraintEqualTo(document.TrailingAnchor),
            _stack.BottomAnchor.ConstraintEqualTo(document.BottomAnchor),
            document.WidthAnchor.ConstraintEqualTo(_scroll.ContentView.WidthAnchor),
        ]);
        _empty = NSTextField.CreateLabel("Select a book to see its details");
        _empty.TextColor = NSColor.TertiaryLabel;
        var root = new NSView();
        foreach (var view in new NSView[] { _scroll, _empty })
        {
            view.TranslatesAutoresizingMaskIntoConstraints = false;
            root.AddSubview(view);
        }
        NSLayoutConstraint.ActivateConstraints(
        [
            _scroll.TopAnchor.ConstraintEqualTo(root.SafeAreaLayoutGuide.TopAnchor),
            _scroll.LeadingAnchor.ConstraintEqualTo(root.LeadingAnchor),
            _scroll.TrailingAnchor.ConstraintEqualTo(root.TrailingAnchor),
            _scroll.BottomAnchor.ConstraintEqualTo(root.BottomAnchor),
            _empty.CenterXAnchor.ConstraintEqualTo(root.CenterXAnchor),
            _empty.CenterYAnchor.ConstraintEqualTo(root.CenterYAnchor),
        ]);
        View = root;
        Show(null);
    }

    /// <summary>The Library page whose selected book the inspector shows, or none while another page shows.</summary>
    public void Follow(LibraryViewModel? library)
    {
        // A folded pane's view isn't loaded until it opens, and the details are built into it.
        _ = View;
        _page.Dispose();
        _library = library;
        if (library is null)
        {
            Show(null);
            return;
        }
        _page.Property(library, nameof(LibraryViewModel.Inspector), () => Show(library.Inspector));
    }

    /// <summary>Details to show when the inspector is opened, without opening it, as for the book being read.</summary>
    public void Hold(InspectorViewModel inspector)
    {
        _ = View;
        Show(inspector, reveal: false);
    }

    void Show(InspectorViewModel? inspector, bool reveal = true)
    {
        if (_stack is null) return;
        _shown.Dispose();
        _shown = new Bindings();
        _scroll.Hidden = inspector is null;
        _empty.Hidden = inspector is not null;
        if (reveal) Showing?.Invoke(this, inspector is not null);
        if (inspector is null)
        {
            Clear();
            return;
        }
        _shown.Properties(inspector, [nameof(InspectorViewModel.Fields), nameof(InspectorViewModel.Facts), nameof(InspectorViewModel.Stages),
            nameof(InspectorViewModel.ShowAllFields), nameof(InspectorViewModel.IsReprocessing), nameof(InspectorViewModel.IsCorrecting)], () => Build(inspector));
    }

    void Clear()
    {
        foreach (var view in _stack.ArrangedSubviews) view.RemoveFromSuperview();
    }

    void Build(InspectorViewModel inspector)
    {
        Clear();
        var item = inspector.Item;
        var cover = new NSImageView { ImageScaling = NSImageScale.ProportionallyUpOrDown };
        cover.HeightAnchor.ConstraintEqualTo(260).Active = true;
        if (item.CoverName is { } name)
        {
            cover.Image = covers.TryGet(name)!;
            if (cover.Image is null) _ = LoadCoverAsync(cover, name);
        }
        Add(cover, stretch: true);

        var title = NSTextField.CreateWrappingLabel(inspector.Title);
        title.Font = NSFont.SystemFontOfSize(18, NSFontWeight.Bold)!;
        Add(title, stretch: true);
        var meta = NSTextField.CreateWrappingLabel(item.Meta);
        meta.TextColor = NSColor.SecondaryLabel;
        Add(meta, stretch: true);

        var actions = new List<NSView>();
        if (inspector.CanOpen && _library is { } library)
            actions.Add(Button("Open", () => library.OpenBookCommand.Execute(item)));
        var reprocess = Button(inspector.ReprocessLabel, () => { });
        _shown.Command(reprocess, inspector.ReprocessCommand);
        actions.Add(reprocess);
        if (!inspector.IsCorrecting) actions.Add(Button("Cataloged wrong…", () => inspector.StartCorrectingCommand.Execute(null)));
        // In the Library, Close leaves the book's details; beside a book being read, it folds the pane away.
        actions.Add(Button("Close", () =>
        {
            if (_library is { } library) library.CloseDetailsCommand.Execute(null);
            else Showing?.Invoke(this, false);
        }));
        var row = NSStackView.FromViews([.. actions]);
        row.Spacing = 8;
        Add(row);

        Section("Details");
        if (inspector.IsCorrecting)
        {
            var banner = NSTextField.CreateWrappingLabel("Correct what's wrong: Keep a value that's right, Edit one that isn't, or Use one Bibliotaph found elsewhere. Done when finished.");
            banner.TextColor = NSColor.SecondaryLabel;
            Add(banner, stretch: true);
            var done = Button("Done", () => { });
            _shown.Command(done, inspector.FinishCorrectingCommand);
            Add(done);
        }
        foreach (var field in inspector.Fields.Where(f => (f.IsKnown || inspector.IsCorrecting) && (inspector.ShowAllFields || f.IsPrimary)))
            Add(FieldRow(field, inspector.IsCorrecting), stretch: true);
        var more = Button(inspector.MoreFieldsLabel, () => inspector.ToggleAllFieldsCommand.Execute(null));
        more.BezelStyle = NSBezelStyle.Inline;
        Add(more);

        if (inspector.Facts.Count > 0)
        {
            Section("The file");
            foreach (var fact in inspector.Facts) Fact(fact.Label, fact.Value, null);
        }

        if (inspector.Locations.Count > 0)
        {
            Section("Where it is");
            foreach (var location in inspector.Locations)
            {
                Fact(location.State.Length > 0 ? location.State : "File", location.Path, null);
                if (location.CanShowInExplorer)
                    Add(Button(location.IsMissing ? "Show last location" : "Show in Finder", () => inspector.ShowInExplorerCommand.Execute(location)));
            }
        }

        if (inspector.HasStages)
        {
            Section("Reading it");
            foreach (var stage in inspector.Stages) Fact(stage.Name, stage.Status, null);
        }
    }

    /// <summary>
    /// A field with what it is, marked when Bibliotaph only suggests it. Edit types a value in its place; while
    /// correcting, Keep confirms a suggestion, Reset goes back to what Bibliotaph found, and each other value it found
    /// has Use.
    /// </summary>
    NSStackView FieldRow(MetadataFieldViewModel field, bool correcting)
    {
        var name = NSTextField.CreateLabel(field.Label);
        name.TextColor = NSColor.SecondaryLabel;
        name.Font = NSFont.SystemFontOfSize(11)!;
        var value = NSTextField.CreateWrappingLabel("");
        value.Selectable = true;
        var edit = Button("Edit", () => field.EditCommand.Execute(null));
        var keep = Button("Keep", () => { });
        _shown.Command(keep, field.KeepCommand);
        var reset = Button("Reset", () => { });
        _shown.Command(reset, field.ResetCommand);
        var buttons = NSStackView.FromViews([edit, keep, reset]);
        buttons.Spacing = 6;

        var text = new NSTextField { PlaceholderString = field.EditHint };
        text.Changed += (_, _) => field.EditText = text.StringValue;
        var save = Button("Save", () => { });
        _shown.Command(save, field.SaveCommand);
        text.Activated += (_, _) =>
        {
            if (field.SaveCommand.CanExecute(null)) field.SaveCommand.Execute(null);
        };
        var cancel = Button("Cancel", () => field.CancelEditCommand.Execute(null));
        var editRow = NSStackView.FromViews([text, save, cancel]);
        editRow.Spacing = 6;
        var note = NSTextField.CreateWrappingLabel("");
        note.Font = NSFont.SystemFontOfSize(11)!;

        var row = NSStackView.FromViews([name, value, buttons, editRow, note]);
        row.Orientation = NSUserInterfaceLayoutOrientation.Vertical;
        row.Alignment = NSLayoutAttribute.Leading;
        row.Spacing = 3;
        if (correcting)
            foreach (var other in field.Alternatives)
            {
                var use = Button($"Use “{other.Text}”", () => field.UseCommand.Execute(other));
                use.BezelStyle = NSBezelStyle.Inline;
                row.AddArrangedSubview(use);
            }

        _shown.Properties(field, [nameof(MetadataFieldViewModel.IsEditing), nameof(MetadataFieldViewModel.Problem), nameof(MetadataFieldViewModel.Resolution),
            nameof(MetadataFieldViewModel.IsBusy)], () =>
        {
            value.StringValue = field.IsKnown ? field.Display + (field.IsSuggested ? $"  ·  {field.EvidenceLabel}" : "") : "—";
            value.TextColor = field.IsSuggested ? NSColor.ControlAccent : NSColor.Label;
            editRow.Hidden = !field.IsEditing;
            buttons.Hidden = field.IsEditing;
            keep.Hidden = !correcting || !field.IsKnown || !field.IsSuggested;
            reset.Hidden = !correcting || !field.CanReset;
            if (field.IsEditing && text.CurrentEditor is null)
            {
                text.StringValue = field.EditText ?? "";
                Bindings.OnMain(() => text.Window?.MakeFirstResponder(text));
            }
            note.StringValue = field.Problem ?? field.Resolution;
            note.TextColor = field.Problem is null ? NSColor.SecondaryLabel : NSColor.SystemRed;
            note.Hidden = !field.IsEditing || note.StringValue.Length == 0;
            row.AlphaValue = field.IsBusy ? 0.6f : 1;
        });
        text.WidthAnchor.ConstraintGreaterThanOrEqualTo(150).Active = true;
        return row;
    }

    async Task LoadCoverAsync(NSImageView view, string name)
    {
        var image = await covers.LoadAsync(name);
        Bindings.OnMain(() => view.Image = image!);
    }

    void Section(string text)
    {
        var label = NSTextField.CreateLabel(text);
        label.Font = NSFont.SystemFontOfSize(13, NSFontWeight.Semibold)!;
        _stack.AddArrangedSubview(label);
        _stack.SetCustomSpacing(4, label);
    }

    void Fact(string label, string value, string? evidence)
    {
        var name = NSTextField.CreateLabel(label);
        name.TextColor = NSColor.SecondaryLabel;
        name.Font = NSFont.SystemFontOfSize(11)!;
        var text = NSTextField.CreateWrappingLabel(evidence is null ? value : $"{value}  ·  {evidence}");
        text.Selectable = true;
        if (evidence is not null) text.TextColor = NSColor.ControlAccent;
        var pair = NSStackView.FromViews([name, text]);
        pair.Orientation = NSUserInterfaceLayoutOrientation.Vertical;
        pair.Alignment = NSLayoutAttribute.Leading;
        pair.Spacing = 1;
        Add(pair, stretch: true);
    }

    void Add(NSView view, bool stretch = false)
    {
        _stack.AddArrangedSubview(view);
        if (stretch) view.WidthAnchor.ConstraintEqualTo(_stack.WidthAnchor, 1, -32).Active = true;
    }

    static NSButton Button(string title, Action action)
    {
        var button = NSButton.CreateButton(title, action);
        button.ControlSize = NSControlSize.Small;
        return button;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _page.Dispose();
            _shown.Dispose();
        }
        base.Dispose(disposing);
    }

    sealed class FlippedView : NSView
    {
        public override bool IsFlipped => true;
    }
}
