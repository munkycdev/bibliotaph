using System.Reflection;
using System.Text.RegularExpressions;
using AppKit;
using Bibliotaph.Mac.Binding;
using Bibliotaph.Presentation.Services;
using Bibliotaph.Presentation.ViewModels;
using CommunityToolkit.Mvvm.Input;
using CoreGraphics;

namespace Bibliotaph.Mac.Views;

/// <summary>
/// Needs review: the suggestions Bibliotaph wants a word on, one card each, and the files it couldn't read. A metadata
/// card has the Windows card's Accept, Edit and Reject; every other kind shows its question and its answers as buttons,
/// so a new kind of card shows here without a view of its own. A decided card says what was done and offers Undo.
/// </summary>
public sealed partial class ReviewController(NeedsReviewViewModel review) : NSViewController, IWindowTitle
{
    /// <summary>Needs review, with how many wait under it.</summary>
    public void FollowTitle(Bindings bindings, Action<string, string> show) =>
        bindings.Property(review, nameof(NeedsReviewViewModel.Subtitle), () => show("Needs review", review.Subtitle));

    readonly Bindings _bindings = new();
    Bindings _cards = new();
    NSStackView _list = null!;

    public override void LoadView()
    {
        var tabs = NSSegmentedControl.FromLabels(["", ""], NSSegmentSwitchTracking.SelectOne, () => { });
        tabs.Activated += (_, _) => review.IsSuggestionsTab = tabs.SelectedSegment == 0;
        var show = new NSPopUpButton();
        show.Activated += (_, _) =>
        {
            var index = (int)show.IndexOfSelectedItem;
            if (index >= 0 && index < review.ShowChoices.Count) review.Show = review.ShowChoices[index];
        };
        var reload = NSButton.CreateButton("Check again", () => { });
        _bindings.Command(reload, review.ReloadCommand);
        // The page's name and how many wait are in the toolbar; the two lists to switch between and their controls start it.
        var controls = NSStackView.FromViews([tabs, new NSView(), show, reload]);
        controls.Spacing = 10;
        var top = new NSStackView { Orientation = NSUserInterfaceLayoutOrientation.Vertical, Alignment = NSLayoutAttribute.Leading, Spacing = 10 };
        top.AddArrangedSubview(controls);
        controls.WidthAnchor.ConstraintEqualTo(top.WidthAnchor, 1, -48).Active = true;
        top.EdgeInsets = new NSEdgeInsets(12, 24, 8, 24);

        _list = new NSStackView { Orientation = NSUserInterfaceLayoutOrientation.Vertical, Alignment = NSLayoutAttribute.Leading, Spacing = 12 };
        _list.EdgeInsets = new NSEdgeInsets(8, 24, 24, 24);
        var scroll = new NSScrollView { HasVerticalScroller = true, DrawsBackground = false, DocumentView = Flipped(_list) };

        var root = new NSView();
        foreach (var view in new NSView[] { top, scroll })
        {
            view.TranslatesAutoresizingMaskIntoConstraints = false;
            root.AddSubview(view);
        }
        NSLayoutConstraint.ActivateConstraints(
        [
            top.TopAnchor.ConstraintEqualTo(root.SafeAreaLayoutGuide.TopAnchor),
            top.LeadingAnchor.ConstraintEqualTo(root.LeadingAnchor),
            top.TrailingAnchor.ConstraintEqualTo(root.TrailingAnchor),
            scroll.TopAnchor.ConstraintEqualTo(top.BottomAnchor),
            scroll.LeadingAnchor.ConstraintEqualTo(root.LeadingAnchor),
            scroll.TrailingAnchor.ConstraintEqualTo(root.TrailingAnchor),
            scroll.BottomAnchor.ConstraintEqualTo(root.BottomAnchor),
            scroll.DocumentView!.WidthAnchor.ConstraintEqualTo(scroll.ContentView.WidthAnchor),
        ]);
        View = root;

        _bindings.Properties(review, [nameof(NeedsReviewViewModel.Subtitle), nameof(NeedsReviewViewModel.Remaining), nameof(NeedsReviewViewModel.HasFiles),
            nameof(NeedsReviewViewModel.IsSuggestionsTab)], () =>
        {
            tabs.SetLabel(review.SuggestionsTabLabel, 0);
            tabs.SetLabel(review.FilesTabLabel, 1);
            tabs.SelectedSegment = review.IsSuggestionsTab ? 0 : 1;
            tabs.Hidden = !review.HasFiles && review.IsSuggestionsTab;
            show.Hidden = !review.IsSuggestionsTab || !review.HasShowChoices;
        });
        void Choices()
        {
            show.RemoveAllItems();
            foreach (var choice in review.ShowChoices) show.AddItem(choice.Label);
            if (review.Show is { } current && review.ShowChoices.IndexOf(current) is var at and >= 0) show.SelectItem(at);
            show.Hidden = !review.IsSuggestionsTab || !review.HasShowChoices;
        }
        _bindings.Collection(review.ShowChoices, _ => Choices());
        _bindings.Property(review, nameof(NeedsReviewViewModel.Show), Choices);
        _bindings.Collection(review.Cards, _ => Rebuild());
        _bindings.Collection(review.Files, _ => Rebuild());
        _bindings.Properties(review, [nameof(NeedsReviewViewModel.IsSuggestionsTab), nameof(NeedsReviewViewModel.IsLoading),
            nameof(NeedsReviewViewModel.Hidden)], Rebuild);
    }

    void Rebuild()
    {
        _cards.Dispose();
        _cards = new Bindings();
        foreach (var view in _list.ArrangedSubviews) view.RemoveFromSuperview();
        if (review.IsLoading && review.Cards.Count == 0)
        {
            _list.AddArrangedSubview(Label("Looking for what needs a word from you…", secondary: true));
            return;
        }
        if (review.IsSuggestionsTab)
        {
            if (review.ShowEmpty) _list.AddArrangedSubview(Label("Nothing needs review. Bibliotaph will put anything it isn't sure of here.", secondary: true));
            foreach (var card in review.Cards) Add(card is ReviewCardViewModel metadata ? MetadataCard(metadata) : GenericCard(card));
            if (review.HasHidden)
            {
                var more = NSButton.CreateButton(review.MoreLabel, () => { });
                _cards.Command(more, review.ShowMoreCommand);
                _list.AddArrangedSubview(more);
            }
        }
        else
        {
            if (review.CanRetryAll)
            {
                var all = NSButton.CreateButton("Try all again", () => { });
                _cards.Command(all, review.RetryAllCommand);
                _list.AddArrangedSubview(all);
            }
            foreach (var group in review.Files) Add(FileCard(group));
        }
    }

    void Add(NSView card)
    {
        _list.AddArrangedSubview(card);
        card.WidthAnchor.ConstraintEqualTo(_list.WidthAnchor, 1, -48).Active = true;
    }

    /// <summary>A field of a book: what it is now, what Bibliotaph suggests and why, with Accept, Accept all, Edit and Reject.</summary>
    CardView MetadataCard(ReviewCardViewModel card)
    {
        var body = Stack();
        // What the card is about first, as a badge: the field, then the book it's on.
        var badgeText = NSTextField.CreateLabel(card.FieldLabel);
        badgeText.Font = NSFont.SystemFontOfSize(12, NSFontWeight.Semibold)!;
        badgeText.TextColor = Palette.AccentText;
        var badge = new Tinted(badgeText, NSColor.ControlAccent, 10, new NSEdgeInsets(3, 10, 3, 10));
        var book = Label(card.Title, secondary: true);
        var head = Row(badge, book);
        head.Alignment = NSLayoutAttribute.CenterY;
        body.AddArrangedSubview(head);
        if (card.Reason.Length > 0) body.AddArrangedSubview(Label(card.Reason, secondary: true));
        // Now and suggested side by side in a column of their own; the suggestion on the palette's soft green.
        body.AddArrangedSubview(Compare(card.CurrentLabel, Label($"{Or(card.CurrentText)}{(card.CurrentSource.Length > 0 ? $"  ·  {card.CurrentSource}" : "")}")));
        if (card.HasSuggestion)
        {
            var suggested = Label(card.SuggestedText);
            suggested.Font = NSFont.SystemFontOfSize(13, NSFontWeight.Medium)!;
            body.AddArrangedSubview(Compare(card.SuggestedLabel, new Tinted(suggested, Palette.Soft, 6, new NSEdgeInsets(3, 8, 3, 8))));
        }
        if (card.HasEvidence) body.AddArrangedSubview(Label(card.Evidence, secondary: true, italic: true));

        var accept = Button("Accept", card.AcceptCommand);
        var acceptAll = Button(card.AcceptAllLabel, card.AcceptAllCommand);
        var edit = Button(card.EditLabel, card.EditCommand);
        var reject = Button(card.RejectLabel, card.RejectCommand);
        var open = Button("Open the book", card.OpenCommand);
        var actions = Row(accept, acceptAll, edit, reject, open);

        var field = new NSTextField { PlaceholderString = card.EditHint };
        field.WidthAnchor.ConstraintGreaterThanOrEqualTo(320).Active = true;
        field.Changed += (_, _) => card.EditText = field.StringValue;
        field.Activated += (_, _) => Run(card.SaveCommand);
        var save = Button("Save", card.SaveCommand);
        save.KeyEquivalent = "\r";
        var cancel = Button("Cancel", card.CancelEditCommand);
        var problem = Label("", tint: NSColor.SystemRed);
        var resolution = Label("", secondary: true);
        var editor = Stack(Row(field, save, cancel), resolution, problem);
        body.AddArrangedSubview(actions);
        body.AddArrangedSubview(editor);

        _cards.Properties(card, [nameof(ReviewCardViewModel.IsEditing), nameof(ReviewCardViewModel.Problem), nameof(ReviewCardViewModel.Resolution)], () =>
        {
            editor.Hidden = !card.IsEditing;
            accept.Hidden = !card.HasSuggestion || !card.CanAccept || card.IsEditing;
            acceptAll.Hidden = !card.HasSuggestion || !card.CanAcceptAll || card.IsEditing;
            reject.Hidden = !card.CanReject || card.IsEditing;
            edit.Hidden = card.IsEditing;
            problem.StringValue = card.Problem ?? "";
            problem.Hidden = card.Problem is null;
            resolution.StringValue = card.Resolution;
            if (card.IsEditing && field.CurrentEditor is null) field.StringValue = card.EditText ?? "";
        });
        return Decided(card, body);
    }

    /// <summary>A value with its name in a narrow column before it: "Current", "Suggested".</summary>
    static NSStackView Compare(string name, NSView value)
    {
        var label = NSTextField.CreateLabel(name);
        label.Font = NSFont.SystemFontOfSize(11)!;
        label.TextColor = NSColor.SecondaryLabel;
        label.WidthAnchor.ConstraintEqualTo(72).Active = true;
        var row = NSStackView.FromViews([label, value]);
        row.Spacing = 8;
        row.Alignment = NSLayoutAttribute.CenterY;
        return row;
    }

    /// <summary>Any other card: its texts, then a button for each answer it offers.</summary>
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "The app isn't trimmed (LinkMode None).")]
    CardView GenericCard(DecidedCardViewModel card)
    {
        var body = Stack();
        var type = card.GetType();
        var shown = new HashSet<string>();
        foreach (var (name, bold, secondary) in TextProperties)
        {
            if (type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static) is not { PropertyType: var t } property || t != typeof(string)) continue;
            if (property.GetValue(card) is not string { Length: > 0 } text || !shown.Add(text)) continue;
            body.AddArrangedSubview(Label(text, bold: bold, secondary: secondary));
        }
        var buttons = new List<NSView>();
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.Name.EndsWith("Command", StringComparison.Ordinal) || Skipped.Contains(property.Name)) continue;
            if (property.GetValue(card) is not IRelayCommand command) continue;
            var name = property.Name[..^"Command".Length];
            if (IsTakingParameter(property.PropertyType))
            {
                // An answer about one of several things (which book a handout goes with): a button for each of them.
                var parameter = property.PropertyType.GetGenericArguments()[0];
                foreach (var item in ItemsOf(card, type, parameter))
                    buttons.Add(Button($"{Words(name)}: {TitleOf(item)}", command, () => item));
                continue;
            }
            var label = type.GetProperty(name + "Label")?.GetValue(card) as string ?? Words(name);
            buttons.Add(Button(label, command));
        }
        if (buttons.Count > 0) body.AddArrangedSubview(Row([.. buttons]));
        return Decided(card, body);
    }

    /// <summary>The card, or once it's decided, what was done with Undo.</summary>
    CardView Decided(DecidedCardViewModel card, NSStackView body)
    {
        var done = Label("", secondary: true);
        var undo = Button("Undo", card.UndoCommand);
        var decided = Row(done, undo);
        var box = Card(Stack(body, decided));
        _cards.Properties(card, [nameof(DecidedCardViewModel.IsDone), nameof(DecidedCardViewModel.DoneText), nameof(DecidedCardViewModel.IsBusy)], () =>
        {
            body.Hidden = card.IsDone;
            decided.Hidden = !card.IsDone;
            done.StringValue = card.DoneText;
            box.AlphaValue = card.IsBusy ? 0.6f : 1;
        });
        return box;
    }

    /// <summary>A file Bibliotaph couldn't read, or a group of them for the same reason, with Try again.</summary>
    CardView FileCard(AttentionGroup group)
    {
        var body = Stack(Label(group.Heading, bold: true), Label(group.Detail, secondary: true));
        if (group.IsMany)
            foreach (var row in group.Rows.Take(12)) body.AddArrangedSubview(Label(row.Title, secondary: true));
        var buttons = new List<NSView>();
        if (group.CanRetry) buttons.Add(Button("Try again", review.RetryGroupCommand, () => group));
        if (group.CanUnlock) buttons.Add(Button("Enter password", review.UnlockCommand, () => group.First));
        if (group.CanOpenElsewhere) buttons.Add(Button("Open in another app", review.OpenElsewhereCommand, () => group.First));
        if (group.IsBug) buttons.Add(Button(group.CopyLabel, review.CopyDetailsCommand, () => group));
        if (buttons.Count > 0) body.AddArrangedSubview(Row([.. buttons]));
        return Card(body);
    }

    /// <summary>The texts a card can have, in the order a card reads.</summary>
    static readonly (string Name, bool Bold, bool Secondary)[] TextProperties =
    [
        ("Title", true, false), ("Heading", false, false), ("Label", true, false), ("Chip", false, true), ("Named", false, false),
        ("Detail", false, true), ("Evidence", false, true), ("Example", false, true), ("Path", false, true), ("BookPath", false, true),
    ];

    /// <summary>Undo is the decided card's; a pop-out reader is the Windows card's other way to open.</summary>
    static readonly HashSet<string> Skipped = ["UndoCommand", "OpenInWindowCommand", "OpenFileInWindowCommand", "OpenBookInWindowCommand"];

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2070", Justification = "The app isn't trimmed (LinkMode None).")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("AOT", "IL3050", Justification = "The app isn't compiled ahead of time.")]
    static IEnumerable<object> ItemsOf(object card, Type type, Type item) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => typeof(IEnumerable<>).MakeGenericType(item).IsAssignableFrom(p.PropertyType))
            .Select(p => p.GetValue(card))
            .OfType<System.Collections.IEnumerable>()
            .FirstOrDefault()?.Cast<object>() ?? [];

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "The app isn't trimmed (LinkMode None).")]
    static string TitleOf(object item) =>
        item.GetType().GetProperty("Title")?.GetValue(item) as string ?? item.GetType().GetProperty("Label")?.GetValue(item) as string ?? item.ToString() ?? "";

    static bool IsTakingParameter(Type command) =>
        command.IsGenericType && command.GetGenericArguments() is [var parameter] && parameter != typeof(object);

    [GeneratedRegex("(?<=[a-z])(?=[A-Z])")]
    private static partial Regex WordBreak();

    /// <summary>"KeepAsCopy" as "Keep as copy", for an answer whose card names it no other way.</summary>
    static string Words(string name)
    {
        var words = WordBreak().Split(name);
        return string.Join(' ', words.Select((w, i) => i == 0 ? w : w.ToLowerInvariant()));
    }

    /// <summary>A rounded panel as tall as what's in it, which a stack of cards lays out one under another.</summary>
    static CardView Card(NSView content)
    {
        var card = new CardView();
        content.TranslatesAutoresizingMaskIntoConstraints = false;
        card.AddSubview(content);
        NSLayoutConstraint.ActivateConstraints(
        [
            content.TopAnchor.ConstraintEqualTo(card.TopAnchor, 12),
            content.LeadingAnchor.ConstraintEqualTo(card.LeadingAnchor, 14),
            content.TrailingAnchor.ConstraintEqualTo(card.TrailingAnchor, -14),
            content.BottomAnchor.ConstraintEqualTo(card.BottomAnchor, -12),
        ]);
        return card;
    }

    static string Or(string text) => text.Length > 0 ? text : "—";

    NSButton Button(string title, System.Windows.Input.ICommand command, Func<object?>? parameter = null)
    {
        var button = NSButton.CreateButton(title, () => { });
        button.ControlSize = NSControlSize.Small;
        _cards.Command(button, command, parameter);
        return button;
    }

    static void Run(System.Windows.Input.ICommand command)
    {
        if (command.CanExecute(null)) command.Execute(null);
    }

    static NSTextField Label(string text, bool bold = false, bool secondary = false, bool italic = false, NSColor? tint = null)
    {
        var label = NSTextField.CreateWrappingLabel(text);
        label.Font = bold ? NSFont.BoldSystemFontOfSize(13)! : italic ? NSFontManager.SharedFontManager.ConvertFont(NSFont.SystemFontOfSize(12)!, NSFontTraitMask.Italic)! : NSFont.SystemFontOfSize(secondary ? 12 : 13)!;
        if (tint is not null) label.TextColor = tint;
        else if (secondary) label.TextColor = NSColor.SecondaryLabel;
        label.Selectable = true;
        return label;
    }

    static NSStackView Stack(params NSView[] views)
    {
        var stack = NSStackView.FromViews(views);
        stack.Orientation = NSUserInterfaceLayoutOrientation.Vertical;
        stack.Alignment = NSLayoutAttribute.Leading;
        stack.Spacing = 6;
        return stack;
    }

    static NSStackView Row(params NSView[] views)
    {
        var row = NSStackView.FromViews(views);
        row.Spacing = 8;
        return row;
    }

    static FlippedView Flipped(NSView content)
    {
        var document = new FlippedView { TranslatesAutoresizingMaskIntoConstraints = false };
        content.TranslatesAutoresizingMaskIntoConstraints = false;
        document.AddSubview(content);
        NSLayoutConstraint.ActivateConstraints(
        [
            content.TopAnchor.ConstraintEqualTo(document.TopAnchor),
            content.LeadingAnchor.ConstraintEqualTo(document.LeadingAnchor),
            content.TrailingAnchor.ConstraintEqualTo(document.TrailingAnchor),
            content.BottomAnchor.ConstraintEqualTo(document.BottomAnchor),
        ]);
        return document;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cards.Dispose();
            _bindings.Dispose();
        }
        base.Dispose(disposing);
    }

    sealed class FlippedView : NSView
    {
        public override bool IsFlipped => true;
    }

    /// <summary>Its colors are set as it draws, so they follow light and dark as the appearance changes.</summary>
    sealed class CardView : NSView
    {
        public CardView()
        {
            WantsLayer = true;
        }

        public override bool WantsUpdateLayer => true;

        public override void UpdateLayer()
        {
            Layer!.CornerRadius = 10;
            Layer.BorderWidth = 1;
            Layer.BorderColor = NSColor.Separator.CGColor;
            Layer.BackgroundColor = NSColor.ControlBackground.CGColor;
        }
    }
}
