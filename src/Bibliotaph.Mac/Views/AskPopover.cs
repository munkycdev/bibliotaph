using AppKit;
using Bibliotaph.Classification;
using Bibliotaph.Mac.Binding;
using Bibliotaph.Presentation.ViewModels;
using CoreGraphics;
using Foundation;

namespace Bibliotaph.Mac.Views;

/// <summary>
/// Ask, beside the toolbar's search: a request in plain English read as a search, shown as the search it became for
/// the user to change or run, or a question answered from the library's pages with each citation going to its book.
/// </summary>
sealed class AskPopover : NSViewController
{
    readonly PlainSearchViewModel _ask;
    readonly Bindings _bindings = new();
    readonly NSPopover _popover;
    NSStackView _content = null!;
    NSTextView _answer = null!;

    readonly Func<string> _question;

    public AskPopover(PlainSearchViewModel ask, Func<string> question)
    {
        _ask = ask;
        _question = question;
        _popover = new NSPopover { ContentViewController = this, Behavior = NSPopoverBehavior.Transient };
    }

    public override void LoadView()
    {
        _content = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Vertical,
            Alignment = NSLayoutAttribute.Leading,
            Spacing = 8,
            EdgeInsets = new NSEdgeInsets(14, 14, 14, 14),
        };
        _answer = new NSTextView { Editable = false, Selectable = true, DrawsBackground = false, Delegate = new Links(this) };
        _answer.WidthAnchor.ConstraintEqualTo(380).Active = true;
        View = _content;
        _content.WidthAnchor.ConstraintEqualTo(410).Active = true;

        _bindings.Properties(_ask, [nameof(PlainSearchViewModel.State), nameof(PlainSearchViewModel.Problem), nameof(PlainSearchViewModel.Message)], Rebuild);
        _bindings.Collection(_ask.Parts, _ => Rebuild());
        _bindings.Properties(_ask.Library, [nameof(LibraryAskViewModel.Answer), nameof(LibraryAskViewModel.Step), nameof(LibraryAskViewModel.Message),
            nameof(LibraryAskViewModel.HasPassages)], Rebuild);
    }

    /// <summary>Shows what Ask has to say under the search field, once it has something.</summary>
    public void ShowUnder(NSView anchor)
    {
        _ = View;
        if (!_popover.Shown) _popover.Show(anchor.Bounds, anchor, NSRectEdge.MaxYEdge);
    }

    public void Close() => _popover.Close();

    void Rebuild()
    {
        foreach (var view in _content.ArrangedSubviews) view.RemoveFromSuperview();
        Text(_ask.Message, secondary: true);
        if (_ask.NeedsAi) Button("Set Up AI…", () => _ask.OpenAiSettingsCommand.Execute(null));
        if (_ask.HasParts)
        {
            Heading("Searching for");
            foreach (var part in _ask.Parts)
            {
                var remove = NSButton.CreateButton("×", () => _ask.RemoveCommand.Execute(part));
                remove.BezelStyle = NSBezelStyle.Inline;
                var line = NSStackView.FromViews([NSTextField.CreateLabel($"{part.Name} {part.Value}"), remove]);
                _content.AddArrangedSubview(line);
            }
            foreach (var note in _ask.Notes) Text(note, secondary: true);
            Button("Search", () => _ask.SearchCommand.Execute(null));
        }
        if (_ask.IsInterpreted || _ask.State == PlainSearchState.Problem)
            Button("Answer from My Books Instead", () => _ask.AnswerCommand.Execute(_question()));

        var library = _ask.Library;
        Text(library.Step, secondary: true);
        Text(library.Message, secondary: true);
        if (library.HasAnswer)
        {
            Heading("Answer");
            ShowAnswer(library.Answer);
            _content.AddArrangedSubview(_answer);
        }
        if (library.HasPassages)
        {
            Heading(library.PassagesHeading);
            foreach (var row in library.Passages)
            {
                var link = NSButton.CreateButton(row.Link, () => library.GoToCommand.Execute(row));
                link.BezelStyle = NSBezelStyle.Inline;
                _content.AddArrangedSubview(link);
            }
        }
        if (library.IsAnswering) Button("Stop", () => library.StopCommand.Execute(null));
        _popover.ContentSize = _content.FittingSize;
    }

    void ShowAnswer(IReadOnlyList<AnswerPart> parts)
    {
        var text = new NSMutableAttributedString();
        for (var i = 0; i < parts.Count; i++)
        {
            var part = parts[i];
            var attributes = new NSStringAttributes { Font = NSFont.SystemFontOfSize(13)!, ForegroundColor = NSColor.Label };
            if (part.Kind == AnswerPartKind.Unsupported) attributes.ForegroundColor = NSColor.SecondaryLabel;
            var piece = new NSMutableAttributedString(new NSAttributedString(part.Text, attributes));
            if (part.Kind == AnswerPartKind.Citation)
                piece.AddAttribute(NSStringAttributeKey.Link, new NSUrl($"bt-cite:{i.ToString(System.Globalization.CultureInfo.InvariantCulture)}"), new NSRange(0, piece.Length));
            text.Append(piece);
        }
        _answer.TextStorage!.SetString(text);
        _answer.LayoutManager!.EnsureLayoutForTextContainer(_answer.TextContainer!);
        var height = _answer.LayoutManager.GetUsedRect(_answer.TextContainer!).Height;
        _answer.SetFrameSize(new CGSize(380, height + 8));
    }

    internal void Cite(int index)
    {
        var parts = _ask.Library.Answer;
        if (index >= 0 && index < parts.Count) _ask.Library.OpenCitationCommand.Execute(parts[index]);
    }

    void Heading(string text)
    {
        var label = NSTextField.CreateLabel(text);
        label.Font = NSFont.SystemFontOfSize(12, NSFontWeight.Semibold)!;
        _content.AddArrangedSubview(label);
    }

    void Text(string? text, bool secondary)
    {
        if (string.IsNullOrEmpty(text)) return;
        var label = NSTextField.CreateWrappingLabel(text);
        label.PreferredMaxLayoutWidth = 380;
        if (secondary) label.TextColor = NSColor.SecondaryLabel;
        _content.AddArrangedSubview(label);
    }

    void Button(string title, Action action) => _content.AddArrangedSubview(NSButton.CreateButton(title, action));

    protected override void Dispose(bool disposing)
    {
        if (disposing) _bindings.Dispose();
        base.Dispose(disposing);
    }

    /// <summary>A citation's link opens its book at its page rather than a browser.</summary>
    sealed class Links(AskPopover owner) : NSTextViewDelegate
    {
        public override bool LinkClicked(NSTextView textView, NSObject link, nuint charIndex)
        {
            var address = (link as NSUrl)?.AbsoluteString ?? link.ToString() ?? "";
            if (address.StartsWith("bt-cite:", StringComparison.Ordinal)
                && int.TryParse(address["bt-cite:".Length..], System.Globalization.CultureInfo.InvariantCulture, out var index))
                owner.Cite(index);
            return true;
        }
    }
}
