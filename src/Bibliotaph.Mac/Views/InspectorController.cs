using System.Windows.Input;
using AppKit;
using Bibliotaph.Mac.Binding;
using Bibliotaph.Presentation.ViewModels;
using CoreGraphics;

namespace Bibliotaph.Mac.Views;

/// <summary>
/// The book's details beside the grid or the book being read, ordered as the Windows panel orders them: what it is and
/// Open first, then Details and Notes to switch between. Details fold into sections, most useful first: at a glance,
/// what's inside and what it goes with, its collections; where it came from, the file, its copies and how far reading
/// it got start folded, as Finder's Get Info folds what's looked at less, and each stays as it was last left.
/// </summary>
public sealed class InspectorController(MacCovers covers) : NSViewController
{
    const float NameWidth = 84;

    readonly Bindings _page = new();
    Bindings _shown = new();
    Bindings _built = new();
    NSStackView _stack = null!;
    NSScrollView _scroll = null!;
    NSTextField _empty = null!;
    LibraryViewModel? _library;
    InspectorViewModel? _inspector;
    NSView? _notes;
    bool _pending;

    /// <summary>The inspector opened or closed, for the split view to show or fold its pane.</summary>
    public event EventHandler<bool>? Showing;

    public override void LoadView()
    {
        _stack = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Vertical,
            Alignment = NSLayoutAttribute.Leading,
            Spacing = 8,
            EdgeInsets = new NSEdgeInsets(16, 16, 24, 16),
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
        // What was typed in the last book's note is kept before its details go.
        _inspector?.Notes?.Flush();
        _inspector = inspector;
        _shown.Dispose();
        _shown = new Bindings();
        _notes = null;
        _scroll.Hidden = inspector is null;
        _empty.Hidden = inspector is not null;
        if (reveal) Showing?.Invoke(this, inspector is not null);
        if (inspector is null)
        {
            _built.Dispose();
            Clear();
            return;
        }
        Build(inspector);
        // The note being typed isn't rebuilt under the typing as the book's other details change.
        _shown.Properties(inspector, [nameof(InspectorViewModel.Fields), nameof(InspectorViewModel.Facts), nameof(InspectorViewModel.Stages),
            nameof(InspectorViewModel.ShowAllFields), nameof(InspectorViewModel.IsReprocessing), nameof(InspectorViewModel.IsCorrecting),
            nameof(InspectorViewModel.Collections), nameof(InspectorViewModel.SourceBook), nameof(InspectorViewModel.IsPickingBook),
            nameof(InspectorViewModel.Parts), nameof(InspectorViewModel.Chapters), nameof(InspectorViewModel.ShowsAllChapters),
            nameof(InspectorViewModel.LookupFacts), nameof(InspectorViewModel.LookAlikeOffer), nameof(InspectorViewModel.RpgGeekRecord)], () =>
        {
            if (!inspector.IsNotesTab) Rebuild(inspector);
        });
        _shown.Property(inspector, nameof(InspectorViewModel.IsNotesTab), () => Rebuild(inspector));
        if (inspector.Pane is { } pane)
            _shown.Property(pane, "", () =>
            {
                if (!inspector.IsNotesTab) Rebuild(inspector);
            });
    }

    // Several details change together as a book is reread; they're shown once, together.
    void Rebuild(InspectorViewModel inspector)
    {
        if (_pending) return;
        _pending = true;
        NSApplication.SharedApplication.BeginInvokeOnMainThread(() =>
        {
            _pending = false;
            if (_inspector == inspector) Build(inspector);
        });
    }

    void Clear()
    {
        foreach (var view in _stack.ArrangedSubviews) view.RemoveFromSuperview();
    }

    void Build(InspectorViewModel inspector)
    {
        _built.Dispose();
        _built = new Bindings();
        Clear();
        Header(inspector);
        if (inspector.Notes is { } notes)
        {
            var tabs = NSSegmentedControl.FromLabels(["Details", notes.TabLabel], NSSegmentSwitchTracking.SelectOne, () => { });
            tabs.SegmentDistribution = NSSegmentDistribution.FillEqually;
            tabs.SelectedSegment = inspector.IsNotesTab ? 1 : 0;
            tabs.Activated += (_, _) => inspector.IsNotesTab = tabs.SelectedSegment == 1;
            _built.Properties(notes, [nameof(EntryNotesViewModel.Text), nameof(EntryNotesViewModel.HasPageNotes)], () => tabs.SetLabel(notes.TabLabel, 1));
            Add(tabs, stretch: true);
            _stack.SetCustomSpacing(14, tabs);
            if (inspector.IsNotesTab)
            {
                NotesView(notes);
                return;
            }
        }
        if (inspector.IsCorrecting) Correcting(inspector);
        if (inspector.HasLookAlikeOffer) LookAlikes(inspector);

        var pane = inspector.Pane;
        if (inspector.GlanceFields.Count > 0 || inspector.HasHiddenGlanceFields)
            Fold("At a Glance", pane?.Glance ?? true, open => pane?.Glance = open, () =>
            {
                Fields(inspector.GlanceFields, inspector.IsCorrecting);
                if (inspector.HasHiddenGlanceFields) More(inspector);
            });
        if (inspector.HasParts)
            Fold(inspector.PartsHeading, pane?.Adventures ?? true, open => pane?.Adventures = open, () =>
            {
                foreach (var part in inspector.Parts) Link(part.Title, part.PagesText, () => inspector.ShowPartCommand.Execute(part));
            });
        if (inspector.IsPart)
            Fold("In a File", pane?.InFile ?? true, open => pane?.InFile = open, () =>
            {
                Text(inspector.PartOf, secondary: true);
                Add(Small("Show the Whole File", () => inspector.ShowPartFileCommand.Execute(null)));
            });
        if (inspector.ShowsGoesWith || inspector.HasCompanions)
            Fold("Goes With", pane?.GoesWith ?? true, open => pane?.GoesWith = open, () => GoesWith(inspector));
        if (inspector.ShowsInside)
            Fold("Inside", pane?.Inside ?? true, open => pane?.Inside = open, () => Inside(inspector));
        Fold("Collections", pane?.Collections ?? true, open => pane?.Collections = open, () => Collections(inspector));
        if (inspector.PublicationFields.Count > 0 || inspector.HasHiddenPublicationFields || inspector.HasRpgGeekRecord)
            Fold("Publication", pane?.Publication ?? false, open => pane?.Publication = open, () =>
            {
                Fields(inspector.PublicationFields, inspector.IsCorrecting);
                if (inspector.HasRpgGeekRecord) Pair("RPGGeek", inspector.RpgGeekText, () => inspector.OpenRpgGeekCommand.Execute(null));
                if (inspector.HasHiddenPublicationFields) More(inspector);
            });
        if (inspector.Facts.Count > 0 || inspector.Locations.Count > 0)
            Fold(inspector.FactsHeading, pane?.File ?? false, open => pane?.File = open, () => File(inspector));
        if (inspector.HasCopies)
            Fold(inspector.CopiesHeading, pane?.Copies ?? false, open => pane?.Copies = open, () => Copies(inspector));
        if (inspector.ShowsRead)
            Fold("Reading It", pane?.Read ?? false, open => pane?.Read = open, () =>
            {
                foreach (var stage in inspector.Stages) Pair(stage.Name, stage.Status);
                foreach (var fact in inspector.LookupFacts) Pair(fact.Label, fact.Value);
            });
    }

    /// <summary>The cover, what kind of book it is, its title and who made it, then Open and a menu of the rest.</summary>
    void Header(InspectorViewModel inspector)
    {
        var item = inspector.Item;
        // A book without a cover starts with its title rather than an empty frame.
        if (item.CoverName is { } name)
        {
            var cover = new NSImageView { ImageScaling = NSImageScale.ProportionallyUpOrDown };
            cover.HeightAnchor.ConstraintEqualTo(200).Active = true;
            cover.Image = covers.TryGet(name)!;
            if (cover.Image is null) _ = LoadCoverAsync(cover, name);
            Add(cover, stretch: true);
            _stack.SetCustomSpacing(14, cover);
        }

        // What kind of book it is, unless that's only its format, which the line under the title starts with.
        if (!item.Meta.StartsWith(inspector.Eyebrow, StringComparison.OrdinalIgnoreCase))
        {
            var eyebrow = NSTextField.CreateLabel(inspector.Eyebrow.ToUpperInvariant());
            eyebrow.Font = NSFont.SystemFontOfSize(10, NSFontWeight.Semibold)!;
            eyebrow.TextColor = NSColor.SecondaryLabel;
            Add(eyebrow, stretch: true);
            _stack.SetCustomSpacing(2, eyebrow);
        }
        var title = NSTextField.CreateWrappingLabel(inspector.Title);
        title.Font = NSFont.SystemFontOfSize(17, NSFontWeight.Bold)!;
        title.Selectable = true;
        Add(title, stretch: true);
        _stack.SetCustomSpacing(3, title);
        if (item.Byline.Length > 0)
        {
            var byline = Label(item.Byline, 12);
            byline.TextColor = NSColor.SecondaryLabel;
            Add(byline, stretch: true);
            _stack.SetCustomSpacing(2, byline);
        }
        var meta = Label(item.Meta, 11);
        meta.TextColor = NSColor.TertiaryLabel;
        Add(meta, stretch: true);
        _stack.SetCustomSpacing(12, meta);

        var actions = new List<NSView>();
        if (inspector.CanOpen && _library is { } library)
        {
            var open = NSButton.CreateButton("Open", () => library.OpenBookCommand.Execute(item));
            open.TintProminence = NSTintProminence.Primary;
            open.WidthAnchor.ConstraintGreaterThanOrEqualTo(90).Active = true;
            actions.Add(open);
        }
        var more = NSButton.CreateButton(NSImage.GetSystemSymbol("ellipsis", "More")!, () => { });
        more.ToolTip = "More";
        more.Activated += (_, _) => MoreMenu(inspector).PopUpMenu(null, new CGPoint(0, more.Bounds.Height + 4), more);
        actions.Add(more);
        var row = Row([.. actions]);
        Add(row);
        _stack.SetCustomSpacing(14, row);
    }

    /// <summary>What's done less often: pin it, read it again, correct its cataloguing, show the file, remove it.</summary>
    NSMenu MoreMenu(InspectorViewModel inspector)
    {
        var menu = new NSMenu();
        void Item(string title, ICommand command, object? parameter = null)
        {
            // What can't be done now is left out rather than dimmed, as the Mac's menus do.
            if (command.CanExecute(parameter)) menu.AddItem(new NSMenuItem(title, (_, _) => command.Execute(parameter)));
        }
        if (_library is { } library) Item(inspector.PinLabel, library.TogglePinBookCommand, inspector.Item);
        if (!inspector.IsCorrecting) Item("Cataloged Wrong…", inspector.StartCorrectingCommand);
        Item(inspector.ReprocessLabel, inspector.ReprocessCommand);
        if (inspector.CanOcrEveryPage) Item("Reprocess with OCR on Every Page", inspector.ReprocessWithOcrCommand);
        if (inspector.Locations.FirstOrDefault(l => l.CanShowInExplorer && !l.IsMissing) is { } location)
            Item("Show in Finder", inspector.ShowInExplorerCommand, location);
        if (_library is not null)
        {
            if (menu.Count > 0) menu.AddItem(NSMenuItem.SeparatorItem);
            Item("Remove from Library…", inspector.RemoveFromLibraryCommand);
        }
        return menu;
    }

    void Correcting(InspectorViewModel inspector)
    {
        var text = Label("Keep each value that's right, edit one that isn't, or use one Bibliotaph found elsewhere.", 12);
        var done = NSButton.CreateButton("Done", () => { });
        done.TintProminence = NSTintProminence.Primary;
        _built.Command(done, inspector.FinishCorrectingCommand);
        var box = Column(Bold("Correcting the Cataloguing"), text, done);
        Add(new Tinted(box, Palette.Soft, 8, new NSEdgeInsets(10, 12, 10, 12)), stretch: true);
    }

    void LookAlikes(InspectorViewModel inspector)
    {
        var detail = Label(inspector.LookAlikeDetail, 11);
        detail.TextColor = NSColor.SecondaryLabel;
        var box = Column(Label(inspector.LookAlikeQuestion, 12), detail,
            Row(Small("Correct Them Too", () => inspector.AcceptLookAlikesCommand.Execute(null)),
                Small("Not Now", () => inspector.DeclineLookAlikesCommand.Execute(null))));
        Add(new Tinted(box, Palette.Soft, 8, new NSEdgeInsets(10, 12, 10, 12)), stretch: true);
    }

    /// <summary>The book it goes with, kept or changed, the books it might go with, and what goes with it.</summary>
    void GoesWith(InspectorViewModel inspector)
    {
        if (inspector.HasGoesWith)
        {
            Link(inspector.GoesWithTitle, inspector.GoesWithHow, () => inspector.ShowSourceBookCommand.Execute(null));
            var buttons = new List<NSView>();
            if (inspector.CanKeepGoesWith) buttons.Add(Small("Keep", () => inspector.KeepSourceBookCommand.Execute(null)));
            buttons.Add(Small("Change…", () => inspector.ChooseSourceBookCommand.Execute(null)));
            buttons.Add(Small("Not This", () => inspector.RefuseSourceBookCommand.Execute(null)));
            if (!inspector.IsPickingBook) Add(Row([.. buttons]));
        }
        else if (inspector.HasProposedBooks)
        {
            Text("It might go with:", secondary: true);
            foreach (var book in inspector.ProposedBooks)
                Link(book.Title, book.Detail, () => inspector.PickSourceBookCommand.Execute(book));
        }
        if (!inspector.HasGoesWith && inspector.ShowsGoesWith && !inspector.IsPickingBook)
            Add(Small("Choose a Book…", () => inspector.ChooseSourceBookCommand.Execute(null)));
        if (inspector.IsPickingBook) PickBook(inspector);
        if (inspector.HasCompanions)
        {
            if (inspector.ShowsGoesWith) Text(inspector.CompanionsHeading, secondary: true);
            foreach (var companion in inspector.Companions)
                Link(companion.Title, companion.How, () => inspector.ShowCompanionCommand.Execute(companion));
        }
    }

    // Typing filters the books under the field; the list changes, the field stays where the typing is.
    void PickBook(InspectorViewModel inspector)
    {
        var find = new NSSearchField { PlaceholderString = "Find the book by title", StringValue = inspector.BookQuery };
        find.Changed += (_, _) => inspector.BookQuery = find.StringValue;
        Add(find, stretch: true);
        var choices = Column();
        Add(choices, stretch: true);
        _built.Property(inspector, nameof(InspectorViewModel.BookChoices), () =>
        {
            foreach (var view in choices.ArrangedSubviews) view.RemoveFromSuperview();
            foreach (var choice in inspector.BookChoices.Take(8))
                choices.AddArrangedSubview(LinkView(choice.Title, choice.Detail, () => inspector.PickSourceBookCommand.Execute(choice)));
        });
        Add(Small("Cancel", () => inspector.CancelPickingBookCommand.Execute(null)));
        Bindings.OnMain(() => find.Window?.MakeFirstResponder(find));
    }

    void Inside(InspectorViewModel inspector)
    {
        Fields(inspector.InsideFields, inspector.IsCorrecting);
        if (inspector.HasHiddenInsideFields && inspector.GlanceFields.Count == 0) More(inspector);
        foreach (var chapter in inspector.ShownChapters)
            Pair(chapter.PagesText, chapter.Lines.Count > 0 ? $"{chapter.Title}\n{string.Join(" · ", chapter.Lines)}" : chapter.Title);
        if (inspector.CanShowAllChapters)
        {
            var all = Small(inspector.ShowAllChaptersLabel, () => inspector.ShowAllChaptersCommand.Execute(null));
            all.BezelStyle = NSBezelStyle.Inline;
            Indented(all);
        }
    }

    /// <summary>The collections it's in, as chips with a × to take it out, and Add to Collection.</summary>
    void Collections(InspectorViewModel inspector)
    {
        if (!inspector.HasCollections) Text("In no collection yet.", secondary: true);
        else
        {
            var chips = new WrapView { Gap = 6 };
            chips.SetItems(inspector.Collections.Select(chip =>
            {
                var name = NSTextField.CreateLabel(chip.Name);
                name.Font = NSFont.SystemFontOfSize(12)!;
                name.ToolTip = chip.Path;
                NSView content = name;
                if (_library is { } library)
                {
                    var remove = NSButton.CreateButton(NSImage.GetSystemSymbol("xmark", "Remove")!, () => library.RemoveFromInspectorCollectionCommand.Execute(chip));
                    remove.Bordered = false;
                    remove.SymbolConfiguration = NSImageSymbolConfiguration.Create(9, NSFontWeight.Bold);
                    remove.ContentTintColor = NSColor.SecondaryLabel;
                    remove.ToolTip = $"Take it out of {chip.Name}";
                    var row = Row(name, remove);
                    row.Spacing = 4;
                    content = row;
                }
                return (NSView)new Tinted(content, Palette.Soft, 10, new NSEdgeInsets(3, 9, 3, 7));
            }));
            Add(chips, stretch: true);
        }
        if (_library is { } owner)
        {
            var add = Small("Add to Collection…", () => { });
            add.Activated += (_, _) =>
            {
                var menu = new NSMenu();
                foreach (var choice in ((ICollectionChoices)owner).MenuChoices)
                    menu.AddItem(new NSMenuItem(choice.Label, (_, _) => owner.AddToCollection(new CollectionRequest(inspector, choice))));
                menu.PopUpMenu(null, new CGPoint(0, add.Bounds.Height + 4), add);
            };
            Add(add);
        }
    }

    void File(InspectorViewModel inspector)
    {
        foreach (var fact in inspector.Facts) Pair(fact.Label, fact.Value);
        foreach (var location in inspector.Locations)
        {
            Pair(location.State.Length > 0 ? location.State : "Where", location.Path);
            if (location.CanShowInExplorer)
                Indented(Small(location.IsMissing ? "Show Last Location" : "Show in Finder", () => inspector.ShowInExplorerCommand.Execute(location)));
        }
    }

    void Copies(InspectorViewModel inspector)
    {
        foreach (var copy in inspector.Copies)
        {
            Pair(copy.IsCurrent ? "Opens" : "Copy", $"{copy.Heading}\n{copy.Detail}");
            var buttons = new List<NSView>();
            if (copy.CanMakeCurrent) buttons.Add(Small("Open This One", () => inspector.MakeCurrentCommand.Execute(copy)));
            if (copy.CanShowInExplorer) buttons.Add(Small("Show in Finder", () => inspector.ShowCopyInExplorerCommand.Execute(copy)));
            if (!copy.IsCurrent) buttons.Add(Small("Not the Same Book", () => inspector.NotSameBookCommand.Execute(copy)));
            if (buttons.Count > 0) Indented(Row([.. buttons]));
        }
    }

    /// <summary>The note on the book, as it was typed, then its notes on pages, each opening its page.</summary>
    void NotesView(EntryNotesViewModel notes)
    {
        _notes ??= NoteEditor(notes);
        Add(Bold("On This Book"));
        Add(_notes, stretch: true);
        _stack.SetCustomSpacing(16, _notes);
        Add(Bold("On Pages"));
        if (!notes.HasPageNotes)
        {
            Text("Notes on pages, made while reading, show here.", secondary: true);
            return;
        }
        foreach (var page in notes.PageNotes)
        {
            if (_library is { } library) Pair(page.Pages, page.Text, () => library.OpenPageNoteCommand.Execute(page));
            else Pair(page.Pages, page.Text);
        }
    }

    static NSScrollView NoteEditor(EntryNotesViewModel notes)
    {
        var scroll = new NSScrollView { HasVerticalScroller = true, BorderType = NSBorderType.BezelBorder };
        var text = new NSTextView(new CGRect(0, 0, 240, 180))
        {
            RichText = false,
            AllowsUndo = true,
            Font = NSFont.SystemFontOfSize(13)!,
            VerticallyResizable = true,
            HorizontallyResizable = false,
            AutoresizingMask = NSViewResizingMask.WidthSizable,
            MinSize = new CGSize(0, 180),
            MaxSize = new CGSize(nfloat.MaxValue, nfloat.MaxValue),
            TextContainerInset = new CGSize(4, 6),
            Value = notes.Text,
        };
        text.TextContainer!.WidthTracksTextView = true;
        text.TextDidChange += (_, _) => notes.Text = text.Value;
        scroll.DocumentView = text;
        scroll.HeightAnchor.ConstraintEqualTo(180).Active = true;
        return scroll;
    }

    /// <summary>A section that folds open and shut beside its title, as Get Info's do, and opens as it was last left.</summary>
    void Fold(string title, bool isOpen, Action<bool> open, Action content)
    {
        var line = new NSBox { BoxType = NSBoxType.NSBoxSeparator };
        Add(line, stretch: true);
        _stack.SetCustomSpacing(6, line);
        var triangle = new NSButton { BezelStyle = NSBezelStyle.Disclosure, Title = "" };
        triangle.SetButtonType(NSButtonType.OnOff);
        triangle.State = isOpen ? NSCellStateValue.On : NSCellStateValue.Off;
        triangle.Activated += (_, _) => open(triangle.State == NSCellStateValue.On);
        triangle.ToolTip = title;
        var label = Bold(title);
        label.AddGestureRecognizer(new NSClickGestureRecognizer(() => open(!isOpen)));
        var head = Row(triangle, label);
        head.Spacing = 2;
        Add(head);
        _stack.SetCustomSpacing(isOpen ? 8 : 6, head);
        if (isOpen) content();
    }

    void Fields(IEnumerable<MetadataFieldViewModel> fields, bool correcting)
    {
        foreach (var field in fields) Add(FieldRow(field, correcting), stretch: true);
    }

    void More(InspectorViewModel inspector)
    {
        var more = Small(inspector.MoreFieldsLabel, () => inspector.ToggleAllFieldsCommand.Execute(null));
        more.BezelStyle = NSBezelStyle.Inline;
        Indented(more);
    }

    /// <summary>
    /// A field as Get Info lists one: its name, then its value, on the palette's soft green with why under it when
    /// Bibliotaph only suggests it. The pencil types a value in its place; while correcting, Keep confirms a suggestion,
    /// Reset goes back to what Bibliotaph found, and each other value it found has Use.
    /// </summary>
    NSStackView FieldRow(MetadataFieldViewModel field, bool correcting)
    {
        var value = Label("", 13);
        value.Selectable = true;
        var shown = new Tinted(value, null, 5, new NSEdgeInsets(1, 5, 1, 5));
        var why = Label("", 11);
        why.TextColor = NSColor.SecondaryLabel;
        var edit = NSButton.CreateButton(NSImage.GetSystemSymbol("pencil", "Edit")!, () => field.EditCommand.Execute(null));
        edit.Bordered = false;
        edit.ContentTintColor = NSColor.TertiaryLabel;
        edit.ToolTip = $"Edit {field.Label}";
        var keep = Small("Keep", () => { });
        _built.Command(keep, field.KeepCommand);
        var reset = Small("Reset", () => { });
        _built.Command(reset, field.ResetCommand);
        var decide = Row(keep, reset);

        var text = new NSTextField { PlaceholderString = field.EditHint };
        text.Changed += (_, _) => field.EditText = text.StringValue;
        text.Activated += (_, _) =>
        {
            if (field.SaveCommand.CanExecute(null)) field.SaveCommand.Execute(null);
        };
        var save = Small("Save", () => { });
        _built.Command(save, field.SaveCommand);
        var editor = Column(text, Row(save, Small("Cancel", () => field.CancelEditCommand.Execute(null))));
        text.WidthAnchor.ConstraintEqualTo(editor.WidthAnchor).Active = true;
        var note = Label("", 11);

        var column = Column(shown, why, decide, editor, note);
        column.Spacing = 3;
        if (correcting)
            foreach (var other in field.Alternatives)
            {
                var use = Small($"Use “{other.Text}”", () => field.UseCommand.Execute(other));
                use.BezelStyle = NSBezelStyle.Inline;
                column.AddArrangedSubview(use);
            }
        column.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
        column.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        foreach (var view in new NSView[] { shown, why, note })
            view.WidthAnchor.ConstraintLessThanOrEqualTo(column.WidthAnchor).Active = true;
        editor.WidthAnchor.ConstraintEqualTo(column.WidthAnchor).Active = true;

        var row = Row(NameLabel(field.Label), column, edit);
        row.Distribution = NSStackViewDistribution.Fill;
        row.Alignment = NSLayoutAttribute.Top;
        row.Spacing = 6;

        _built.Properties(field, [nameof(MetadataFieldViewModel.IsEditing), nameof(MetadataFieldViewModel.Problem), nameof(MetadataFieldViewModel.Resolution),
            nameof(MetadataFieldViewModel.IsBusy)], () =>
        {
            value.StringValue = field.IsKnown ? field.Display : "—";
            value.TextColor = field.IsKnown ? NSColor.Label : NSColor.TertiaryLabel;
            shown.Fill = field.IsKnown && field.IsSuggested ? Palette.Soft : null;
            shown.Hidden = field.IsEditing;
            why.StringValue = field.IsKnown && field.IsSuggested ? field.EvidenceLabel : "";
            why.Hidden = field.IsEditing || why.StringValue.Length == 0;
            editor.Hidden = !field.IsEditing;
            edit.Hidden = field.IsEditing;
            keep.Hidden = !correcting || !field.IsKnown || !field.IsSuggested;
            reset.Hidden = !correcting || !field.CanReset;
            decide.Hidden = field.IsEditing || (keep.Hidden && reset.Hidden);
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
        return row;
    }

    /// <summary>A plain value beside its name, or one that opens something, as a page note opens its page.</summary>
    void Pair(string label, string value, Action? open = null)
    {
        NSView shown;
        if (open is null)
        {
            var text = Label(value, 13);
            text.Selectable = true;
            shown = text;
        }
        else shown = LinkView(value, "", open);
        shown.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
        shown.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        var row = Row(NameLabel(label), shown);
        row.Distribution = NSStackViewDistribution.Fill;
        row.Alignment = NSLayoutAttribute.FirstBaseline;
        row.Spacing = 11;
        Add(row, stretch: true);
    }

    void Link(string title, string detail, Action open) => Add(LinkView(title, detail, open), stretch: true);

    /// <summary>Something that opens another book or page: its name in the accent colour, and a line about it.</summary>
    static NSStackView LinkView(string title, string detail, Action open)
    {
        var name = Label(title, 13);
        name.TextColor = NSColor.ControlAccent;
        name.AddGestureRecognizer(new NSClickGestureRecognizer(open));
        name.AccessibilityRole = NSAccessibilityRoles.LinkRole;
        var pair = Column(name);
        pair.Spacing = 1;
        if (detail.Length > 0)
        {
            var about = Label(detail, 11);
            about.TextColor = NSColor.SecondaryLabel;
            pair.AddArrangedSubview(about);
        }
        return pair;
    }

    /// <summary>Under the values, past the names' column.</summary>
    void Indented(NSView view)
    {
        var spacer = new NSView();
        spacer.WidthAnchor.ConstraintEqualTo(NameWidth + 3).Active = true;
        Add(Row(spacer, view));
    }

    static NSTextField NameLabel(string text)
    {
        var name = NSTextField.CreateWrappingLabel(text);
        name.TextColor = NSColor.SecondaryLabel;
        name.Font = NSFont.SystemFontOfSize(11)!;
        name.Alignment = NSTextAlignment.Right;
        name.WidthAnchor.ConstraintEqualTo(NameWidth).Active = true;
        return name;
    }

    static NSTextField Label(string text, nfloat size)
    {
        var label = NSTextField.CreateWrappingLabel(text);
        label.Font = NSFont.SystemFontOfSize(size)!;
        return label;
    }

    static NSTextField Bold(string text)
    {
        var label = NSTextField.CreateLabel(text);
        label.Font = NSFont.SystemFontOfSize(13, NSFontWeight.Semibold)!;
        return label;
    }

    void Text(string text, bool secondary)
    {
        var label = Label(text, 12);
        if (secondary) label.TextColor = NSColor.SecondaryLabel;
        Add(label, stretch: true);
    }

    static NSStackView Row(params NSView[] views)
    {
        var row = NSStackView.FromViews(views);
        row.Spacing = 8;
        return row;
    }

    static NSStackView Column(params NSView[] views)
    {
        var column = NSStackView.FromViews(views);
        column.Orientation = NSUserInterfaceLayoutOrientation.Vertical;
        column.Alignment = NSLayoutAttribute.Leading;
        column.Spacing = 6;
        return column;
    }

    static NSButton Small(string title, Action action)
    {
        var button = NSButton.CreateButton(title, action);
        button.ControlSize = NSControlSize.Small;
        return button;
    }

    async Task LoadCoverAsync(NSImageView view, string name)
    {
        var image = await covers.LoadAsync(name);
        Bindings.OnMain(() => view.Image = image!);
    }

    void Add(NSView view, bool stretch = false)
    {
        _stack.AddArrangedSubview(view);
        if (stretch) view.WidthAnchor.ConstraintEqualTo(_stack.WidthAnchor, 1, -32).Active = true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _page.Dispose();
            _shown.Dispose();
            _built.Dispose();
        }
        base.Dispose(disposing);
    }

    sealed class FlippedView : NSView
    {
        public override bool IsFlipped => true;
    }
}
