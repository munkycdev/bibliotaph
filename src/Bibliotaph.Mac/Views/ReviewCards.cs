using System.Windows.Input;
using AppKit;
using Bibliotaph.Mac.Binding;
using Bibliotaph.Presentation.Services;
using Bibliotaph.Presentation.ViewModels;
using Bibliotaph.Processing;

namespace Bibliotaph.Mac.Views;

/// <summary>
/// Each kind of Needs review card laid out as Windows lays it out: what it's about as a title that opens it, the kind of
/// question as a chip, why Bibliotaph asks, and its answers, the likeliest first in the accent, each with the tip Windows
/// gives it. Pickers, ticks and page boxes stand where Windows has its own.
/// </summary>
public sealed partial class ReviewController
{
    /// <summary>The card's own layout, or null for a kind this page doesn't know yet, which then shows its answers as plain buttons.</summary>
    CardView? KindCard(DecidedCardViewModel card) => card switch
    {
        TermCardViewModel term => TermCard(term),
        OverlapCardViewModel overlap => OverlapCard(overlap),
        VersionCardViewModel version => VersionCard(version),
        RevisionCardViewModel revision => RevisionCard(revision),
        ElsewhereCardViewModel elsewhere => ElsewhereCard(elsewhere),
        LookupMatchCardViewModel lookup => LookupCard(lookup),
        SourceBookCardViewModel source => SourceBookCard(source),
        CorrectionCardViewModel correction => CorrectionCard(correction),
        FolderRuleCardViewModel rule => FolderRuleCard(rule),
        FolderCollectionCardViewModel folder => FolderCollectionCard(folder),
        CollectionSuggestionCardViewModel suggestion => SuggestionCard(suggestion),
        PartProposalCardViewModel parts => PartsCard(parts),
        UnplacedPartCardViewModel part => UnplacedCard(part),
        PackCardViewModel pack => PackCard(pack),
        _ => null,
    };

    CardView TermCard(TermCardViewModel card)
    {
        var body = Stack(Head(Heading(card.Label), card.Chip), Label(card.Detail, secondary: true));
        if (card.HasExample) body.AddArrangedSubview(Evidence(card.Example));
        // The term can be filed under one already in the vocabulary, picked here, as another name for it.
        var targets = new NSPopUpButton { ControlSize = NSControlSize.Small, ToolTip = "Another name for an existing term" };
        targets.AddItem("File Under…");
        foreach (var target in card.Targets) targets.AddItem(target.Label);
        targets.Activated += (_, _) =>
        {
            var index = (int)targets.IndexOfSelectedItem - 1;
            card.Target = index >= 0 && index < card.Targets.Count ? card.Targets[index] : null;
        };
        targets.Hidden = card.Targets.Count == 0;
        var map = Answer("File Under It", card.MapCommand, "Another name for the term picked");
        map.Hidden = targets.Hidden;
        body.AddArrangedSubview(Answers(
            Answer(card.AddLabel, card.AddCommand, "", primary: true),
            Answer("Reject", card.RejectCommand, "Not a useful name; it won't be suggested again"),
            Gap(), targets, map));
        return Decided(card, body);
    }

    CardView OverlapCard(OverlapCardViewModel card)
    {
        var body = Stack(Head(Heading(card.Label), card.Chip), Label(card.Detail, secondary: true));
        // The term that stays, the one most books use unless another is picked; the others fold into it.
        var keep = new NSPopUpButton { ControlSize = NSControlSize.Small, ToolTip = VocabularyViewModel.CombineHelp };
        foreach (var term in card.Terms) keep.AddItem(term.ToString());
        keep.SelectItem(card.Terms.ToList().IndexOf(card.Keep));
        keep.Activated += (_, _) =>
        {
            if (keep.IndexOfSelectedItem is var index and >= 0 && index < card.Terms.Count) card.Keep = card.Terms[(int)index];
        };
        body.AddArrangedSubview(Answers(
            keep,
            Answer(VocabularyViewModel.CombineLabel, card.CombineCommand, VocabularyViewModel.CombineHelp, primary: true),
            Gap(),
            Answer(VocabularyViewModel.KeepApartLabel, card.KeepApartCommand, VocabularyViewModel.KeepApartHelp)));
        return Decided(card, body);
    }

    CardView VersionCard(VersionCardViewModel card)
    {
        var body = Stack(
            Head(TitleLink(card.Title, card.OpenFileCommand, card.OpenFileInWindowCommand, "Open this file"), VersionCardText.Chip),
            Label(card.Heading, secondary: true));
        if (card.Path.Length > 0) body.AddArrangedSubview(PathLine(card.Path));
        body.AddArrangedSubview(Evidence(card.Evidence, card.Detail));
        var book = TitleLink("Open the Book", card.OpenBookCommand, card.OpenBookInWindowCommand, card.BookPath.Length > 0 ? card.BookPath : "Open the book");
        book.Font = NSFont.SystemFontOfSize(12)!;
        book.ContentTintColor = NSColor.Link;
        body.AddArrangedSubview(book);
        body.AddArrangedSubview(Answers(
            Answer(FileIsCurrent(card.MakeCurrentLabel), card.MakeCurrentCommand, card.MakeCurrentHint, asWritten: true),
            Answer(FileIsCurrent(card.KeepAsCopyLabel), card.KeepAsCopyCommand, card.KeepAsCopyHint, asWritten: true),
            Answer("Separate Book", card.SeparateBookCommand, VersionCardText.SeparateHint)));
        return Decided(card, body);
    }

    /// <summary>A new version's places: each that needs a look with Open and Use This Page, then those that found their page.</summary>
    CardView RevisionCard(RevisionCardViewModel card)
    {
        var heading = Label(card.Heading, secondary: true);
        _cards.Property(card, nameof(RevisionCardViewModel.Heading), () => heading.StringValue = card.Heading);
        var body = Stack(Head(TitleLink(card.Title, card.OpenBookCommand, card.OpenBookInWindowCommand, "Open the new version"), RevisionCardText.Chip), heading);
        if (card.Path.Length > 0) body.AddArrangedSubview(PathLine(card.Path));
        if (card.Detail.Length > 0) body.AddArrangedSubview(Label(card.Detail, secondary: true));
        foreach (var row in card.Rows) Fill(body, Place(row));
        if (card.HasFound)
        {
            var found = Stack(Small("Found their page, nothing to do:"));
            found.Spacing = 2;
            foreach (var place in card.Found) found.AddArrangedSubview(Row(Symbol("checkmark", NSColor.SecondaryLabel), Small(place, NSColor.Label)));
            if (card.HasFoundMore) found.AddArrangedSubview(Small(card.FoundMore));
            body.AddArrangedSubview(found);
        }
        return Decided(card, body);
    }

    Tinted Place(RevisionRowViewModel row)
    {
        var icon = Symbol("exclamationmark.triangle", NSColor.ControlAccent);
        var heading = NSTextField.CreateLabel(row.Heading);
        heading.Font = NSFont.SystemFontOfSize(12, NSFontWeight.Semibold)!;
        heading.LineBreakMode = NSLineBreakMode.TruncatingTail;
        heading.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        var where = Small("");
        var text = Stack(heading, where);
        text.Spacing = 2;
        text.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
        var open = TitleLink("Open", row.OpenCommand, row.OpenInWindowCommand, "Open the new version at this page");
        open.Font = NSFont.SystemFontOfSize(12)!;
        open.ContentTintColor = NSColor.Link;
        var window = Icon("macwindow.badge.plus", row.OpenInWindowName, row.OpenInWindowCommand);
        var use = Answer("Use This Page", row.UseCommand, RevisionCardText.UseHint);
        var undo = Answer("Undo", row.UndoUseCommand, row.UndoName);
        var line = Row(icon, text, open, window, use, undo);
        line.Alignment = NSLayoutAttribute.CenterY;
        _cards.Properties(row, [nameof(RevisionRowViewModel.IsChecked), nameof(RevisionRowViewModel.IsBusy)], () =>
        {
            icon.Image = NSImage.GetSystemSymbol(row.IsChecked ? "checkmark" : "exclamationmark.triangle", null)!;
            icon.ContentTintColor = row.IsChecked ? NSColor.SecondaryLabel : NSColor.ControlAccent;
            where.StringValue = row.Where;
            use.Hidden = !row.CanUse;
            undo.Hidden = !row.CanUndoUse;
        });
        return new Tinted(line, NSColor.QuaternarySystemFill, 6, new NSEdgeInsets(8, 12, 8, 12));
    }

    CardView ElsewhereCard(ElsewhereCardViewModel card)
    {
        var body = Stack(
            Head(TitleLink(card.Title, card.OpenFileCommand, card.OpenFileInWindowCommand, "Open this file"), ElsewhereCardText.Chip),
            Label(card.Heading, secondary: true));
        if (card.Path.Length > 0) body.AddArrangedSubview(PathLine(card.Path));
        if (card.Detail.Length > 0) body.AddArrangedSubview(Evidence(card.Detail));
        body.AddArrangedSubview(Answers(
            Answer("Same Book", card.SameBookCommand, "One card: the book you own elsewhere, now opening this file. What you set on it stays.", primary: true),
            Answer("Separate Book", card.SeparateBookCommand, "Two cards, as now. Bibliotaph won't ask about these two again.")));
        return Decided(card, body);
    }

    CardView LookupCard(LookupMatchCardViewModel card)
    {
        var body = Stack(
            Head(TitleLink(card.Title, card.OpenFileCommand, card.OpenFileInWindowCommand, "Open this book"), LookupMatchCardText.Chip),
            Label(card.Heading, secondary: true));
        if (card.Path.Length > 0) body.AddArrangedSubview(PathLine(card.Path));
        body.AddArrangedSubview(Evidence(card.Evidence, card.Detail));
        body.AddArrangedSubview(Answers(
            Answer("Same Book", card.SameBookCommand, "Fill in what this record gives, as the catalogue's suggestions. Anything you set yourself stays.", primary: true),
            Answer("Not This Book", card.NotThisBookCommand, "Bibliotaph won't use this record for this book.")));
        return Decided(card, body);
    }

    /// <summary>Maps or handouts and the books they may go with: a button for each book, its reason beside it, and none of these.</summary>
    CardView SourceBookCard(SourceBookCardViewModel card)
    {
        var body = Stack(Head(Heading(card.Title), SourceBookCardText.Chip), Label(card.Heading, secondary: true));
        if (card.Named.Length > 0) body.AddArrangedSubview(Label(card.Named, secondary: true));
        if (card.Path.Length > 0) body.AddArrangedSubview(PathLine(card.Path, card.ShowFolderCommand));
        body.AddArrangedSubview(Evidence(card.Evidence, SourceBookCardViewModel.Detail));
        var books = Stack();
        books.Spacing = 4;
        foreach (var book in card.Books)
        {
            var goes = Answer($"Goes with {book.Title}", card.GoesWithCommand, "It takes the book's system, publisher, setting and tags, and shows inside it.", asWritten: true,
                parameter: () => book);
            goes.Image = NSImage.GetSystemSymbol("checkmark", null)!;
            goes.ImagePosition = NSCellImagePosition.ImageLeading;
            var line = book.Detail.Length > 0 ? Row(goes, Small(book.Detail)) : Row(goes);
            line.Alignment = NSLayoutAttribute.CenterY;
            books.AddArrangedSubview(line);
        }
        books.AddArrangedSubview(Answer(card.NoneLabel, card.NotFromTheseCommand, "It stays as it is, and these books won't be suggested for it again."));
        books.EdgeInsets = new NSEdgeInsets(6, 0, 0, 0);
        body.AddArrangedSubview(books);
        return Decided(card, body);
    }

    CardView CorrectionCard(CorrectionCardViewModel card)
    {
        var body = Stack(
            Head(TitleLink(card.Title, card.OpenFileCommand, card.OpenFileInWindowCommand, "Open this book"), CorrectionCardText.Chip),
            Label(card.Heading, secondary: true), Label(card.Detail, secondary: true));
        var fix = Answer("Fix It", card.FixItCommand, "Open its details, with the values Bibliotaph guessed marked", primary: true);
        fix.Image = NSImage.GetSystemSymbol("pencil", null)!;
        fix.ImagePosition = NSCellImagePosition.ImageLeading;
        body.AddArrangedSubview(Answers(fix, Answer("It's Fine Now", card.ItsFineCommand, "Leave it as it is, and take this card away")));
        return Decided(card, body);
    }

    CardView FolderRuleCard(FolderRuleCardViewModel card)
    {
        var body = Stack(
            Head(FolderLink(card.Title, card.ShowFolderCommand), FolderRuleCardText.Chip, card),
            Label(card.Heading, secondary: true), PathLine(card.Path), Evidence(card.Detail));
        body.AddArrangedSubview(Answers(
            Answer("Use This Rule", card.UseRuleCommand, "Suggest this for every file in the folder, new ones too", primary: true),
            Answer("No Thanks", card.NoRuleCommand, "No rule. Bibliotaph won't suggest this one again.")));
        return Decided(card, body);
    }

    CardView FolderCollectionCard(FolderCollectionCardViewModel card)
    {
        var body = Stack(
            Head(FolderLink(card.Title, card.ShowFolderCommand), FolderCollectionCardText.Chip, card),
            Label(card.Heading, secondary: true), PathLine(card.Path), Evidence(card.Detail));
        body.AddArrangedSubview(Answers(
            Answer("Accept", card.AcceptCommand, "Make the collection, with this folder's books", primary: true),
            Answer("Not Now", card.NotNowCommand, "Ask again the next time this list is made"),
            Answer("Never", card.NeverCommand, "No collection. Bibliotaph won't ask about this folder again.")));
        return Decided(card, body);
    }

    /// <summary>Books that could join a collection, each a link that opens it, Option-click in a window of its own.</summary>
    CardView SuggestionCard(CollectionSuggestionCardViewModel card)
    {
        var body = Stack(Head(Heading(card.Title), CollectionSuggestionCardText.Chip, card), Label(card.Heading, secondary: true));
        var books = Stack();
        books.Spacing = 2;
        foreach (var book in card.Books)
        {
            var link = NSButton.CreateButton(book.Title, () =>
            {
                var own = NSApplication.SharedApplication.CurrentEvent?.ModifierFlags.HasFlag(NSEventModifierMask.AlternateKeyMask) == true;
                if (own) card.OpenBookInWindowCommand.Execute(book);
                else card.OpenBookCommand.Execute(book);
            });
            link.Bordered = false;
            link.Font = NSFont.SystemFontOfSize(12)!;
            link.ContentTintColor = book.DocumentId is null ? NSColor.SecondaryLabel : NSColor.Link;
            link.Enabled = book.DocumentId is not null;
            link.ToolTip = "Open this book (Option-click opens it in a window of its own)";
            books.AddArrangedSubview(link);
        }
        body.AddArrangedSubview(books);
        if (card.Detail.Length > 0) body.AddArrangedSubview(Evidence(card.Detail));
        body.AddArrangedSubview(Answers(
            Answer("Accept", card.AcceptCommand, "Add these books to the collection", primary: true),
            Answer("Not Now", card.NotNowCommand, "Ask again the next time this list is made"),
            Answer("Never", card.NeverCommand, "Bibliotaph won't suggest these books for this collection again")));
        return Decided(card, body);
    }

    /// <summary>The adventures a file seems to hold, each ticked to get a card, with their titles and pages to type over under Edit.</summary>
    CardView PartsCard(PartProposalCardViewModel card)
    {
        var body = Stack(
            Head(TitleLink(card.Title, card.OpenFileCommand, card.OpenFileInWindowCommand, "Open this file"), card.Chip, card),
            Label(card.Heading, secondary: true));
        if (card.Detail.Length > 0) body.AddArrangedSubview(Label(card.Detail, secondary: true));
        var rows = Stack();
        rows.Spacing = 8;
        rows.EdgeInsets = new NSEdgeInsets(6, 0, 0, 0);
        body.AddArrangedSubview(rows);
        Fill(body, rows);
        var problem = Label("", tint: NSColor.SystemOrange);
        body.AddArrangedSubview(problem);
        var accept = Answer(card.AcceptLabel, card.AcceptCommand, PartProposalCardText.AcceptHint, primary: true);
        var edit = Answer("Edit", card.EditCommand, PartProposalCardText.EditHint);
        edit.Image = NSImage.GetSystemSymbol("pencil", null)!;
        edit.ImagePosition = NSCellImagePosition.ImageLeading;
        body.AddArrangedSubview(Answers(accept, edit, Answer("Not Split", card.NotSplitCommand, PartProposalCardText.NotSplitHint)));

        _cards.Property(card, nameof(PartProposalCardViewModel.IsEditing), () =>
        {
            foreach (var view in rows.ArrangedSubviews) view.RemoveFromSuperview();
            foreach (var row in card.Rows) Fill(rows, ProposedPart(row, card.IsEditing));
            edit.Title = card.IsEditing ? "Done" : "Edit";
        });
        _cards.Properties(card, [nameof(PartProposalCardViewModel.Problem), nameof(PartProposalCardViewModel.AcceptLabel)], () =>
        {
            problem.StringValue = card.Problem ?? "";
            problem.Hidden = card.Problem is null;
            accept.Title = card.AcceptLabel;
        });
        return Decided(card, body);
    }

    static NSStackView ProposedPart(ProposedPartRow row, bool editing)
    {
        var tick = NSButton.CreateCheckbox("", () => { });
        tick.State = row.IsChecked ? NSCellStateValue.On : NSCellStateValue.Off;
        tick.Activated += (_, _) => row.IsChecked = tick.State == NSCellStateValue.On;
        tick.ToolTip = $"Make a card for {row.Title}";
        NSView content;
        if (editing)
        {
            var title = Field(row.Title, "The adventure's title", text => row.Title = text);
            var from = Field(row.From, "First", text => row.From = text);
            from.ToolTip = "First page, as the file numbers it";
            var to = Field(row.To, "Last", text => row.To = text);
            to.ToolTip = "Last page, as the file numbers it";
            from.WidthAnchor.ConstraintEqualTo(64).Active = true;
            to.WidthAnchor.ConstraintEqualTo(64).Active = true;
            title.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
            content = Row(title, from, to);
        }
        else
        {
            var title = Label(row.Title);
            title.Font = NSFont.SystemFontOfSize(12, NSFontWeight.Semibold)!;
            var summary = Small(row.Summary);
            content = Stack(title, summary);
            ((NSStackView)content).Spacing = 2;
        }
        var line = Row(tick, content);
        line.Alignment = NSLayoutAttribute.Top;
        content.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
        return line;
    }

    /// <summary>An adventure whose pages weren't found in the file it opens now: Set Its Pages types them in a sheet.</summary>
    CardView UnplacedCard(UnplacedPartCardViewModel card)
    {
        var body = Stack(
            Head(TitleLink(card.Title, card.OpenFileCommand, card.OpenFileInWindowCommand, "Open its file"), UnplacedPartCardText.Chip),
            Label(card.Heading, secondary: true), Label(UnplacedPartCardViewModel.Detail, secondary: true));
        var set = Answer("Set Its Pages…", card.SetPagesCommand, "Type its first and last pages as the file numbers them", primary: true);
        body.AddArrangedSubview(Answers(set,
            Answer("Remove Adventure", card.RemovePartCommand, "Its card goes; its pages are only the file's again. Undo brings it back.")));
        PartSheet.Follow(_cards, card, nameof(UnplacedPartCardViewModel.Editor), () => card.Editor, () => View.Window);
        return Decided(card, body);
    }

    /// <summary>Images that could be one card, with the first few to show what they are.</summary>
    CardView PackCard(PackCardViewModel card)
    {
        var body = Stack(Head(FolderLink(card.Title, card.ShowFolderCommand), card.Chip, card), Label(card.Heading, secondary: true));
        var tiles = new NSStackView { Spacing = 6 };
        foreach (var tile in card.Tiles.Take(8))
        {
            var image = new NSImageView { ImageScaling = NSImageScale.ProportionallyUpOrDown, WantsLayer = true };
            image.Layer!.CornerRadius = 4;
            image.Layer.MasksToBounds = true;
            image.Layer.BackgroundColor = NSColor.QuaternarySystemFill.CGColor;
            image.WidthAnchor.ConstraintEqualTo(56).Active = true;
            image.HeightAnchor.ConstraintEqualTo(56).Active = true;
            image.Image = covers.TryGet(tile.Name)!;
            if (image.Image is null) LoadTileAsync(image, tile.Name).FireAndForget();
            tiles.AddArrangedSubview(image);
        }
        if (tiles.ArrangedSubviews.Length > 0) body.AddArrangedSubview(tiles);
        body.AddArrangedSubview(Evidence(card.Detail));
        body.AddArrangedSubview(Answers(
            Answer("Make a Pack", card.MakePackCommand, "One card for these images. Anything set on an image is kept, and Split gives them back.", primary: true),
            Answer("Keep Separate", card.KeepSeparateCommand, "Each image keeps its own card. Bibliotaph won't ask about this folder again.")));
        return Decided(card, body);
    }

    async Task LoadTileAsync(NSImageView view, string name)
    {
        var image = await covers.LoadAsync(name);
        Bindings.OnMain(() => view.Image = image!);
    }

    /// <summary>
    /// The line every card starts with: what it's about, the kind of question as a chip at the trailing edge, and for an
    /// offer, × that closes it with its "don't ask again" answer, which Undo brings back.
    /// </summary>
    NSStackView Head(NSView title, string chip, OfferCardViewModel? offer = null)
    {
        title.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
        title.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        var views = new List<NSView> { title };
        if (chip.Length > 0)
        {
            var text = NSTextField.CreateLabel(chip);
            text.Font = NSFont.SystemFontOfSize(10, NSFontWeight.Medium)!;
            text.TextColor = NSColor.SecondaryLabel;
            var pill = new Tinted(text, Palette.Soft, 8, new NSEdgeInsets(2, 8, 2, 8));
            pill.SetContentHuggingPriorityForOrientation(750, NSLayoutConstraintOrientation.Horizontal);
            views.Add(pill);
        }
        if (offer is not null) views.Add(Icon("xmark", ReviewCardText.DismissHint, offer.DismissCommand));
        var head = Row([.. views]);
        head.Alignment = NSLayoutAttribute.CenterY;
        head.SetHuggingPriority(1, NSLayoutConstraintOrientation.Horizontal);
        return head;
    }

    static NSTextField Heading(string text)
    {
        var label = NSTextField.CreateWrappingLabel(text);
        label.Font = NSFont.SystemFontOfSize(14, NSFontWeight.Semibold)!;
        label.Selectable = true;
        return label;
    }

    /// <summary>The card's title as a link that opens what it's about; Option-click opens it in a window of its own.</summary>
    static NSButton TitleLink(string title, ICommand open, ICommand? inWindow, string tip)
    {
        var button = NSButton.CreateButton(title, () =>
        {
            var own = NSApplication.SharedApplication.CurrentEvent?.ModifierFlags.HasFlag(NSEventModifierMask.AlternateKeyMask) == true;
            var command = own && inWindow is not null ? inWindow : open;
            if (command.CanExecute(null)) command.Execute(null);
        });
        button.Bordered = false;
        button.Font = NSFont.SystemFontOfSize(14, NSFontWeight.Semibold)!;
        button.ContentTintColor = NSColor.Label;
        button.LineBreakMode = NSLineBreakMode.TruncatingTail;
        button.Alignment = NSTextAlignment.Left;
        button.ToolTip = inWindow is null ? tip : $"{tip} (Option-click opens it in a window of its own)";
        return button;
    }

    static NSButton FolderLink(string title, ICommand show)
    {
        var link = TitleLink(title, show, null, "Show in Finder");
        link.Image = NSImage.GetSystemSymbol("folder", null)!;
        link.ImagePosition = NSCellImagePosition.ImageLeading;
        return link;
    }

    /// <summary>Where the file or folder is, shortened in the middle, whole in its tip; a link to Finder where the card has one.</summary>
    static NSControl PathLine(string path, ICommand? show = null)
    {
        NSControl line;
        if (show is null)
        {
            line = Small(path);
        }
        else
        {
            var button = NSButton.CreateButton(path, () => show.Execute(null));
            button.Bordered = false;
            button.Font = NSFont.SystemFontOfSize(11)!;
            button.ContentTintColor = NSColor.Link;
            line = button;
        }
        line.LineBreakMode = NSLineBreakMode.TruncatingMiddle;
        line.ToolTip = show is null ? path : $"Show in Finder: {path}";
        line.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        return line;
    }

    /// <summary>What Bibliotaph went on, beside a magnifier as Windows marks it; a second line is the quieter detail.</summary>
    static NSStackView Evidence(string text, string detail = "")
    {
        var lines = Stack(Small(text, NSColor.Label, wraps: true));
        lines.Spacing = 3;
        if (detail.Length > 0) lines.AddArrangedSubview(Small(detail, wraps: true));
        lines.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
        var row = Row(Symbol("text.magnifyingglass", NSColor.SecondaryLabel), lines);
        row.Alignment = NSLayoutAttribute.Top;
        row.EdgeInsets = new NSEdgeInsets(6, 0, 0, 0);
        return row;
    }

    /// <summary>An answer: the likeliest one in the accent, as the primary button is on Windows.</summary>
    NSButton Answer(string title, ICommand command, string tip, bool primary = false, Func<object?>? parameter = null, bool asWritten = false)
    {
        var button = Button(asWritten ? title : MacText.TitleCase(title), command, parameter);
        button.ControlSize = NSControlSize.Regular;
        if (tip.Length > 0) button.ToolTip = tip;
        if (primary) button.TintProminence = NSTintProminence.Primary;
        return button;
    }

    /// <summary>"Notes.pdf is current" as "Notes.pdf Is Current": the file's own name is left as it is written.</summary>
    static string FileIsCurrent(string label) =>
        label.EndsWith(" is current", StringComparison.Ordinal) ? label[..^" is current".Length] + " Is Current" : label;

    static NSStackView Answers(params NSView[] views)
    {
        var row = Row(views);
        row.Alignment = NSLayoutAttribute.CenterY;
        row.EdgeInsets = new NSEdgeInsets(8, 0, 0, 0);
        return row;
    }

    static NSView Gap()
    {
        var gap = new NSView();
        gap.WidthAnchor.ConstraintEqualTo(8).Active = true;
        return gap;
    }

    NSButton Icon(string symbol, string tip, ICommand command)
    {
        var button = NSButton.CreateButton(NSImage.GetSystemSymbol(symbol, tip)!, () => { });
        button.Bordered = false;
        button.ContentTintColor = NSColor.SecondaryLabel;
        button.ToolTip = tip;
        _cards.Command(button, command);
        return button;
    }

    static NSImageView Symbol(string name, NSColor tint) => new()
    {
        Image = NSImage.GetSystemSymbol(name, null)!,
        ContentTintColor = tint,
        SymbolConfiguration = NSImageSymbolConfiguration.Create(11, NSFontWeight.Regular),
    };

    static NSTextField Small(string text, NSColor? color = null, bool wraps = false)
    {
        var label = wraps ? NSTextField.CreateWrappingLabel(text) : NSTextField.CreateLabel(text);
        label.Font = NSFont.SystemFontOfSize(11)!;
        label.TextColor = color ?? NSColor.SecondaryLabel;
        if (!wraps) label.LineBreakMode = NSLineBreakMode.TruncatingTail;
        return label;
    }

    static NSTextField Field(string text, string placeholder, Action<string> changed)
    {
        var field = new NSTextField { StringValue = text, PlaceholderString = placeholder, ControlSize = NSControlSize.Small };
        field.Font = NSFont.SystemFontOfSize(12)!;
        field.Changed += (_, _) => changed(field.StringValue);
        return field;
    }

    /// <summary>A view that spans the stack it's added to, for rows whose buttons sit at the trailing edge.</summary>
    static void Fill(NSStackView stack, NSView view)
    {
        if (view.Superview != stack) stack.AddArrangedSubview(view);
        view.WidthAnchor.ConstraintEqualTo(stack.WidthAnchor).Active = true;
    }
}
