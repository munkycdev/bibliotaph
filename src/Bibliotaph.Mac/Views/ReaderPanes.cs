using AppKit;
using Bibliotaph.Mac.Binding;
using Bibliotaph.Presentation.ViewModels;
using CoreGraphics;
using Foundation;
using ObjCRuntime;

namespace Bibliotaph.Mac.Views;

/// <summary>The book's bookmarks beside its pages, indented by level, each going to its page with a click.</summary>
sealed class OutlineList : NSScrollView
{
    readonly ViewerViewModel _reader;
    readonly NSTableView _table;
    IReadOnlyList<OutlineEntry> _entries = [];

    public OutlineList(ViewerViewModel reader)
    {
        _reader = reader;
        _table = new NSTableView { Style = NSTableViewStyle.SourceList, HeaderView = null!, UsesAutomaticRowHeights = true };
        _table.AddColumn(new NSTableColumn("entry"));
        var source = new Source(this);
        _table.DataSource = source;
        _table.Delegate = source;
        DocumentView = _table;
        HasVerticalScroller = true;
        DrawsBackground = false;
    }

    public OutlineList(NativeHandle handle) : base(handle)
    {
        _reader = null!;
        _table = null!;
    }

    public void Show(IReadOnlyList<OutlineEntry> entries)
    {
        if (ReferenceEquals(entries, _entries)) return;
        _entries = entries;
        _table.ReloadData();
    }

    sealed class Source(OutlineList owner) : NSTableViewDelegate, INSTableViewDataSource
    {
        [Export("numberOfRowsInTableView:")]
        public nint GetRowCount(NSTableView tableView) => owner._entries.Count;

        public override NSView GetViewForItem(NSTableView tableView, NSTableColumn tableColumn, nint row)
        {
            var entry = owner._entries[(int)row];
            var title = NSTextField.CreateWrappingLabel(entry.Title);
            title.Font = NSFont.SystemFontOfSize(12, entry.Depth == 0 ? NSFontWeight.Medium : NSFontWeight.Regular)!;
            title.MaximumNumberOfLines = 2;
            var page = NSTextField.CreateLabel(entry.Page);
            page.Font = NSFont.MonospacedDigitSystemFontOfSize(11, NSFontWeight.Regular)!;
            page.TextColor = NSColor.SecondaryLabel;
            var line = NSStackView.FromViews([title, page]);
            line.Spacing = 6;
            line.EdgeInsets = new NSEdgeInsets(3, (nfloat)(4 + entry.Indent), 3, 4);
            title.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
            return line;
        }

        public override void SelectionDidChange(NSNotification notification)
        {
            var row = (int)owner._table.SelectedRow;
            if (row < 0 || row >= owner._entries.Count) return;
            owner._reader.OpenOutlineEntryCommand.Execute(owner._entries[row]);
        }
    }
}

/// <summary>
/// The page strip under the pages: a thumbnail of every page side by side, drawn as they come into view, the page
/// being read marked and kept in sight. A click goes to that page.
/// </summary>
sealed class StripView : NSScrollView
{
    const double Gap = 10;
    readonly PageStripViewModel _strip;
    readonly StripDocument _document = new();
    readonly Bindings _pages = new();
    readonly List<NSImageView> _images = [];
    IReadOnlyList<PageThumbnail> _shown = [];

    public StripView(PageStripViewModel strip)
    {
        _strip = strip;
        DocumentView = _document;
        HasHorizontalScroller = true;
        HasVerticalScroller = false;
        AutohidesScrollers = true;
        DrawsBackground = true;
        BackgroundColor = NSColor.UnderPageBackground;
        ContentView.PostsBoundsChangedNotifications = true;
        NSNotificationCenter.DefaultCenter.AddObserver(NSView.BoundsChangedNotification, _ => Draw(), ContentView);
    }

    public StripView(NativeHandle handle) : base(handle)
    {
        _strip = null!;
    }

    public void Show()
    {
        if (!ReferenceEquals(_shown, _strip.Pages)) Build();
        foreach (var (image, page) in _images.Zip(_shown)) image.Layer!.BorderWidth = page.IsCurrent ? 3 : 0;
        if (_strip.Current is { } current && current.Index < _images.Count)
        {
            var frame = _images[current.Index].Frame;
            if (frame.X < ContentView.Bounds.X || frame.Right > ContentView.Bounds.Right) _document.ScrollRectToVisible(frame.Inset(-40, 0));
        }
        Draw();
    }

    void Build()
    {
        _pages.Dispose();
        foreach (var image in _images) image.Superview?.RemoveFromSuperview();
        _images.Clear();
        _shown = _strip.Pages;
        double x = Gap;
        foreach (var page in _shown)
        {
            var image = new NSImageView { ImageScaling = NSImageScale.ProportionallyUpOrDown, WantsLayer = true, ToolTip = page.Page };
            image.Layer!.BackgroundColor = NSColor.White.CGColor;
            image.Layer.BorderColor = NSColor.ControlAccent.CGColor;
            var label = NSTextField.CreateLabel(page.Page);
            label.Font = NSFont.SystemFontOfSize(10)!;
            label.TextColor = NSColor.SecondaryLabel;
            label.Alignment = NSTextAlignment.Center;
            var cell = new ClickView(() => _strip.GoToPageCommand.Execute(page));
            cell.AddSubview(image);
            cell.AddSubview(label);
            cell.Frame = new CGRect(x, 8, page.Width, page.Height + 20);
            image.Frame = new CGRect(0, 0, page.Width, page.Height);
            label.Frame = new CGRect(0, page.Height + 2, page.Width, 16);
            _document.AddSubview(cell);
            _images.Add(image);
            x += page.Width + Gap;
            var thumbnail = page;
            _pages.Property(page, nameof(PageThumbnail.Image), () => image.Image = (thumbnail.Image as NSImage)!);
        }
        _document.SetFrameSize(new CGSize(Math.Max(x, ContentView.Bounds.Width), 140));
    }

    /// <summary>The thumbnails in view, and a few either side, are drawn.</summary>
    void Draw()
    {
        if (Hidden || _images.Count == 0 || ContentView.Bounds.Width <= 0) return;
        var visible = ContentView.Bounds;
        var first = _images.FindIndex(i => i.Superview!.Frame.Right >= visible.X);
        var last = _images.FindLastIndex(i => i.Superview!.Frame.X <= visible.Right);
        if (first >= 0 && last >= first) _strip.Draw(first, last);
    }

    /// <summary>A strip shown before it had a size draws once it has one.</summary>
    public override void Layout()
    {
        base.Layout();
        Draw();
    }

    public override bool Hidden
    {
        get => base.Hidden;
        set
        {
            base.Hidden = value;
            if (!value) Bindings.OnMain(Draw);
        }
    }

    sealed class StripDocument : NSView
    {
        public override bool IsFlipped => true;
    }

    sealed class ClickView(Action click) : NSView
    {
        public override bool IsFlipped => true;

        public override void MouseUp(NSEvent theEvent) => click();
    }
}

/// <summary>
/// The book's page notes beside its pages: each note with its pages, a click going there, with Edit and Delete, and Add
/// note for the page being read. Adding and editing ask for the pages and the note in a sheet.
/// </summary>
sealed class NotesPane : NSView
{
    readonly PageNotesViewModel _notes;
    readonly Bindings _bindings = new();
    readonly NSStackView _list;

    public NotesPane(PageNotesViewModel notes)
    {
        _notes = notes;
        var heading = NSTextField.CreateLabel("Notes");
        heading.Font = NSFont.SystemFontOfSize(13, NSFontWeight.Semibold)!;
        var add = NSButton.CreateButton("Add note…", () => { });
        add.ControlSize = NSControlSize.Small;
        _bindings.Command(add, notes.AddCommand);
        var top = NSStackView.FromViews([heading, new NSView(), add]);
        _list = new NSStackView { Orientation = NSUserInterfaceLayoutOrientation.Vertical, Alignment = NSLayoutAttribute.Leading, Spacing = 10 };
        var document = new Flipped();
        _list.TranslatesAutoresizingMaskIntoConstraints = false;
        document.AddSubview(_list);
        var scroll = new NSScrollView { DocumentView = document, HasVerticalScroller = true, DrawsBackground = false };
        document.TranslatesAutoresizingMaskIntoConstraints = false;
        foreach (var view in new NSView[] { top, scroll })
        {
            view.TranslatesAutoresizingMaskIntoConstraints = false;
            AddSubview(view);
        }
        NSLayoutConstraint.ActivateConstraints(
        [
            top.TopAnchor.ConstraintEqualTo(TopAnchor, 10),
            top.LeadingAnchor.ConstraintEqualTo(LeadingAnchor, 12),
            top.TrailingAnchor.ConstraintEqualTo(TrailingAnchor, -12),
            scroll.TopAnchor.ConstraintEqualTo(top.BottomAnchor, 8),
            scroll.LeadingAnchor.ConstraintEqualTo(LeadingAnchor),
            scroll.TrailingAnchor.ConstraintEqualTo(TrailingAnchor),
            scroll.BottomAnchor.ConstraintEqualTo(BottomAnchor),
            document.WidthAnchor.ConstraintEqualTo(scroll.ContentView.WidthAnchor),
            _list.TopAnchor.ConstraintEqualTo(document.TopAnchor),
            _list.LeadingAnchor.ConstraintEqualTo(document.LeadingAnchor, 12),
            _list.TrailingAnchor.ConstraintEqualTo(document.TrailingAnchor, -12),
            _list.BottomAnchor.ConstraintEqualTo(document.BottomAnchor),
        ]);
        _bindings.Collection(notes.Items, _ => Show());
        _bindings.Property(notes, nameof(PageNotesViewModel.Dialog), () =>
        {
            if (notes.Dialog is { } dialog && Window is { } window) Ask(dialog, window);
        });
        Show();
    }

    public NotesPane(NativeHandle handle) : base(handle)
    {
        _notes = null!;
        _list = null!;
    }

    void Show()
    {
        foreach (var view in _list.ArrangedSubviews) view.RemoveFromSuperview();
        if (_notes.Items.Count == 0)
        {
            var none = NSTextField.CreateWrappingLabel("No notes on this book yet. Add one for the page you're reading.");
            none.TextColor = NSColor.SecondaryLabel;
            _list.AddArrangedSubview(none);
        }
        foreach (var row in _notes.Items)
        {
            var pages = NSButton.CreateButton(row.Pages, () => _notes.GoToCommand.Execute(row));
            pages.BezelStyle = NSBezelStyle.Inline;
            var text = NSTextField.CreateWrappingLabel(row.Text);
            text.Selectable = true;
            var edit = NSButton.CreateButton("Edit", () => _notes.EditCommand.Execute(row));
            var delete = NSButton.CreateButton("Delete", () => _notes.DeleteCommand.Execute(row));
            edit.ControlSize = delete.ControlSize = NSControlSize.Small;
            var views = new List<NSView> { pages, text };
            if (row.Reason is { } reason)
            {
                var why = NSTextField.CreateWrappingLabel(reason);
                why.TextColor = NSColor.SystemOrange;
                views.Add(why);
            }
            views.Add(NSStackView.FromViews([edit, delete]));
            var note = NSStackView.FromViews([.. views]);
            note.Orientation = NSUserInterfaceLayoutOrientation.Vertical;
            note.Alignment = NSLayoutAttribute.Leading;
            note.Spacing = 4;
            _list.AddArrangedSubview(note);
            text.PreferredMaxLayoutWidth = 240;
        }
    }

    static void Ask(PageNoteDialogViewModel dialog, NSWindow window)
    {
        var alert = new NSAlert { MessageText = dialog.Heading };
        var from = new NSTextField(new CGRect(0, 0, 90, 24)) { StringValue = dialog.From, PlaceholderString = "From page" };
        var to = new NSTextField(new CGRect(0, 0, 90, 24)) { StringValue = dialog.To, PlaceholderString = "To page" };
        var text = new NSTextField(new CGRect(0, 0, 320, 80)) { StringValue = dialog.Text, PlaceholderString = "Your note" };
        text.HeightAnchor.ConstraintEqualTo(80).Active = true;
        text.WidthAnchor.ConstraintEqualTo(320).Active = true;
        var pages = NSStackView.FromViews([from, NSTextField.CreateLabel("to"), to]);
        var stack = NSStackView.FromViews([pages, text]);
        stack.Orientation = NSUserInterfaceLayoutOrientation.Vertical;
        stack.Alignment = NSLayoutAttribute.Leading;
        stack.SetFrameSize(stack.FittingSize);
        alert.AccessoryView = stack;
        alert.Window.InitialFirstResponder = text;
        alert.AddButton(dialog.ActionText).KeyEquivalent = "\r";
        alert.AddButton("Cancel").KeyEquivalent = "\u001b";
        alert.BeginSheet(window, response =>
        {
            if ((long)response == (long)NSAlertButtonReturn.First)
            {
                dialog.From = from.StringValue;
                dialog.To = to.StringValue;
                dialog.Text = text.StringValue;
            }
            if ((long)response == (long)NSAlertButtonReturn.First && dialog.SaveCommand.CanExecute(null)) dialog.SaveCommand.Execute(null);
            else dialog.CancelCommand.Execute(null);
            alert.Dispose();
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _bindings.Dispose();
        base.Dispose(disposing);
    }

    sealed class Flipped : NSView
    {
        public override bool IsFlipped => true;
    }
}

/// <summary>
/// Ask this book beside its pages: a question, the answer as it comes with each citation a link to its page, the
/// pages it was read from, and the questions asked before. It needs a model, which Settings › AI sets up.
/// </summary>
sealed class AskPane : NSView
{
    readonly AskPanelViewModel _ask;
    readonly Bindings _bindings = new();
    readonly NSTextView _answer;
    readonly NSStackView _passages;
    readonly NSStackView _history;

    public AskPane(AskPanelViewModel ask)
    {
        _ask = ask;
        var heading = NSTextField.CreateLabel("Ask this book");
        heading.Font = NSFont.SystemFontOfSize(13, NSFontWeight.Semibold)!;
        var restart = NSButton.CreateButton("New conversation", () => { });
        restart.ControlSize = NSControlSize.Small;
        _bindings.Command(restart, ask.NewConversationCommand);
        var top = NSStackView.FromViews([heading, new NSView(), restart]);

        var question = new NSTextField { PlaceholderString = "Ask a question about this book" };
        question.Changed += (_, _) => ask.Question = question.StringValue;
        question.Activated += (_, _) =>
        {
            ask.Question = question.StringValue;
            if (ask.AskCommand.CanExecute(null)) ask.AskCommand.Execute(null);
        };
        var go = NSButton.CreateButton("Ask", () => { });
        _bindings.Command(go, ask.AskCommand);
        var stop = NSButton.CreateButton("Stop", () => { });
        _bindings.Command(stop, ask.StopCommand);
        var row = NSStackView.FromViews([question, go, stop]);
        row.Spacing = 6;
        question.SetContentHuggingPriorityForOrientation(100, NSLayoutConstraintOrientation.Horizontal);

        var status = NSTextField.CreateWrappingLabel("");
        status.TextColor = NSColor.SecondaryLabel;
        status.Font = NSFont.SystemFontOfSize(11)!;
        var message = NSTextField.CreateWrappingLabel("");
        message.TextColor = NSColor.SystemOrange;
        _answer = new NSTextView { Editable = false, Selectable = true, DrawsBackground = false, Delegate = new Links(this), VerticallyResizable = true };
        _answer.TextContainer!.WidthTracksTextView = true;
        _answer.HeightAnchor.ConstraintGreaterThanOrEqualTo(20).Active = true;
        var passagesTitle = NSTextField.CreateLabel("");
        passagesTitle.Font = NSFont.SystemFontOfSize(12, NSFontWeight.Semibold)!;
        _passages = Column(4);
        var coverage = NSTextField.CreateWrappingLabel("");
        coverage.TextColor = NSColor.TertiaryLabel;
        coverage.Font = NSFont.SystemFontOfSize(11)!;
        _history = Column(10);

        var content = Column(10);
        foreach (var view in new NSView[] { message, status, _answer, passagesTitle, _passages, coverage, _history }) content.AddArrangedSubview(view);
        var document = new Flipped();
        content.TranslatesAutoresizingMaskIntoConstraints = false;
        document.AddSubview(content);
        var scroll = new NSScrollView { DocumentView = document, HasVerticalScroller = true, DrawsBackground = false };
        document.TranslatesAutoresizingMaskIntoConstraints = false;
        foreach (var view in new NSView[] { top, row, scroll })
        {
            view.TranslatesAutoresizingMaskIntoConstraints = false;
            AddSubview(view);
        }
        NSLayoutConstraint.ActivateConstraints(
        [
            top.TopAnchor.ConstraintEqualTo(TopAnchor, 10),
            top.LeadingAnchor.ConstraintEqualTo(LeadingAnchor, 12),
            top.TrailingAnchor.ConstraintEqualTo(TrailingAnchor, -12),
            row.TopAnchor.ConstraintEqualTo(top.BottomAnchor, 8),
            row.LeadingAnchor.ConstraintEqualTo(LeadingAnchor, 12),
            row.TrailingAnchor.ConstraintEqualTo(TrailingAnchor, -12),
            scroll.TopAnchor.ConstraintEqualTo(row.BottomAnchor, 8),
            scroll.LeadingAnchor.ConstraintEqualTo(LeadingAnchor),
            scroll.TrailingAnchor.ConstraintEqualTo(TrailingAnchor),
            scroll.BottomAnchor.ConstraintEqualTo(BottomAnchor),
            document.WidthAnchor.ConstraintEqualTo(scroll.ContentView.WidthAnchor),
            content.TopAnchor.ConstraintEqualTo(document.TopAnchor),
            content.LeadingAnchor.ConstraintEqualTo(document.LeadingAnchor, 12),
            content.TrailingAnchor.ConstraintEqualTo(document.TrailingAnchor, -12),
            content.BottomAnchor.ConstraintEqualTo(document.BottomAnchor),
            _answer.WidthAnchor.ConstraintEqualTo(content.WidthAnchor),
        ]);

        _bindings.Property(ask, nameof(AskPanelViewModel.Question), () =>
        {
            if (question.CurrentEditor is null) question.StringValue = ask.Question;
        });
        _bindings.Properties(ask, [nameof(AskPanelViewModel.Step), nameof(AskPanelViewModel.Asked), nameof(AskPanelViewModel.LookedFor),
            nameof(AskPanelViewModel.IsAsking), nameof(AskPanelViewModel.IsAnswering)], () =>
        {
            status.StringValue = string.Join("\n", new[] { ask.Asked, ask.LookedFor, ask.Step }.Where(t => t is { Length: > 0 }));
            status.Hidden = status.StringValue.Length == 0;
            stop.Hidden = !ask.IsAnswering;
        });
        _bindings.Property(ask, nameof(AskPanelViewModel.Message), () =>
        {
            message.StringValue = ask.Message ?? "";
            message.Hidden = ask.Message is null;
        });
        _bindings.Property(ask, nameof(AskPanelViewModel.Answer), ShowAnswer);
        _bindings.Properties(ask, [nameof(AskPanelViewModel.HasPassages), nameof(AskPanelViewModel.PassagesHeading)], () =>
        {
            passagesTitle.StringValue = ask.PassagesHeading;
            passagesTitle.Hidden = !ask.HasPassages;
        });
        _bindings.Collection(ask.Passages, _ => ShowPassages());
        _bindings.Properties(ask, [nameof(AskPanelViewModel.CoverageText), nameof(AskPanelViewModel.MeaningText)], () =>
        {
            coverage.StringValue = string.Join("\n", new[] { ask.CoverageText, ask.MeaningText }.Where(t => t is { Length: > 0 }));
            coverage.Hidden = coverage.StringValue.Length == 0;
        });
        _bindings.Collection(ask.History, _ => ShowHistory());
        ShowPassages();
        ShowHistory();
    }

    public AskPane(NativeHandle handle) : base(handle)
    {
        _ask = null!;
        _answer = null!;
        _passages = null!;
        _history = null!;
    }

    static NSStackView Column(double spacing) => new()
    {
        Orientation = NSUserInterfaceLayoutOrientation.Vertical,
        Alignment = NSLayoutAttribute.Leading,
        Spacing = (nfloat)spacing,
    };

    /// <summary>The answer's words, its quotes in their marks, and each citation a link that goes to its page.</summary>
    void ShowAnswer()
    {
        var text = new NSMutableAttributedString();
        var font = NSFont.SystemFontOfSize(13)!;
        var parts = _ask.Answer;
        for (var i = 0; i < parts.Count; i++)
        {
            var part = parts[i];
            var attributes = new NSStringAttributes { Font = font, ForegroundColor = NSColor.Label };
            NSUrl? link = null;
            switch (part.Kind)
            {
                case Bibliotaph.Classification.AnswerPartKind.Quote:
                    attributes.Font = NSFontManager.SharedFontManager.ConvertFont(font, NSFontTraitMask.Italic);
                    break;
                case Bibliotaph.Classification.AnswerPartKind.Citation:
                    link = new NSUrl($"bt-cite:{i.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
                    break;
                case Bibliotaph.Classification.AnswerPartKind.Unsupported:
                    attributes.ForegroundColor = NSColor.SecondaryLabel;
                    break;
            }
            var piece = new NSMutableAttributedString(new NSAttributedString(part.Text, attributes));
            if (link is not null) piece.AddAttribute(NSStringAttributeKey.Link, link, new NSRange(0, piece.Length));
            text.Append(piece);
        }
        _answer.TextStorage!.SetString(text);
        _answer.Hidden = parts.Count == 0;
    }

    internal void Cite(int index)
    {
        if (index >= 0 && index < _ask.Answer.Count) _ask.OpenCitationCommand.Execute(_ask.Answer[index]);
    }

    void ShowPassages()
    {
        foreach (var view in _passages.ArrangedSubviews) view.RemoveFromSuperview();
        foreach (var row in _ask.Passages)
        {
            var link = NSButton.CreateButton(row.Link, () => _ask.GoToCommand.Execute(row));
            link.BezelStyle = NSBezelStyle.Inline;
            var text = NSTextField.CreateWrappingLabel(row.Text);
            text.Font = NSFont.SystemFontOfSize(11)!;
            text.TextColor = NSColor.SecondaryLabel;
            text.MaximumNumberOfLines = 4;
            text.PreferredMaxLayoutWidth = 280;
            _passages.AddArrangedSubview(link);
            _passages.AddArrangedSubview(text);
        }
    }

    void ShowHistory()
    {
        foreach (var view in _history.ArrangedSubviews) view.RemoveFromSuperview();
        if (_ask.History.Count == 0) return;
        var title = NSTextField.CreateLabel("Asked before");
        title.Font = NSFont.SystemFontOfSize(12, NSFontWeight.Semibold)!;
        _history.AddArrangedSubview(title);
        foreach (var turn in _ask.History)
        {
            var question = NSTextField.CreateWrappingLabel(turn.Question);
            question.Font = NSFont.SystemFontOfSize(12, NSFontWeight.Medium)!;
            var answer = NSTextField.CreateWrappingLabel(string.Concat(turn.Answer.Select(p => p.Text)));
            answer.Font = NSFont.SystemFontOfSize(11)!;
            answer.TextColor = NSColor.SecondaryLabel;
            answer.MaximumNumberOfLines = 6;
            question.PreferredMaxLayoutWidth = answer.PreferredMaxLayoutWidth = 280;
            _history.AddArrangedSubview(question);
            _history.AddArrangedSubview(answer);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _bindings.Dispose();
        base.Dispose(disposing);
    }

    sealed class Flipped : NSView
    {
        public override bool IsFlipped => true;
    }

    /// <summary>A citation's link goes to its page in the book rather than to a browser.</summary>
    sealed class Links(AskPane owner) : NSTextViewDelegate
    {
        public override bool LinkClicked(NSTextView textView, NSObject link, nuint charIndex)
        {
            var address = (link as NSUrl)?.AbsoluteString ?? link.ToString() ?? "";
            if (!address.StartsWith("bt-cite:", StringComparison.Ordinal)) return true;
            if (int.TryParse(address["bt-cite:".Length..], System.Globalization.CultureInfo.InvariantCulture, out var index)) owner.Cite(index);
            return true;
        }
    }
}
