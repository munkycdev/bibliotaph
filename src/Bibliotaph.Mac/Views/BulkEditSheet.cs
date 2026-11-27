using AppKit;
using Bibliotaph.Mac.Binding;
using Bibliotaph.Presentation.ViewModels;
using CoreGraphics;

namespace Bibliotaph.Mac.Views;

/// <summary>
/// Edit metadata for the ticked books, as the Windows bulk editor: each field with what the books have now and the
/// change to make, then Review changes for what will happen before anything is saved, then Apply.
/// </summary>
static class BulkEditSheet
{
    public static (Sheet, Bindings) Show(BulkEditViewModel editor, NSWindow host)
    {
        var sheet = new Sheet(620);
        var bindings = new Bindings();
        var heading = sheet.Text(editor.Heading);
        heading.Font = NSFont.SystemFontOfSize(17, NSFontWeight.Semibold)!;

        var fields = Column(16);
        fields.EdgeInsets = new NSEdgeInsets(4, 0, 12, 12);
        foreach (var field in editor.Fields.Prepend<object>(editor.GoesWith))
        {
            NSStackView? row = field switch
            {
                BulkGoesWithViewModel goesWith => GoesWith(goesWith, bindings),
                BulkChipsFieldViewModel chips => Chips(chips, bindings),
                BulkValueFieldViewModel value => Value(value, bindings),
                _ => null,
            };
            if (row is null) continue;
            fields.AddArrangedSubview(row);
            row.WidthAnchor.ConstraintEqualTo(fields.WidthAnchor, 1, -12).Active = true;
        }
        var scroll = Scrolling(fields, 460);
        sheet.AddWide(scroll);

        var summary = Column(8);
        sheet.AddWide(summary);
        bindings.Property(editor, nameof(BulkEditViewModel.Summary), () =>
        {
            foreach (var view in summary.ArrangedSubviews) view.RemoveFromSuperview();
            summary.AddArrangedSubview(PageParts.Section("What Will Change"));
            foreach (var line in editor.Summary)
            {
                var text = Label($"{line.Field}: {line.Text}", 13);
                text.PreferredMaxLayoutWidth = 560;
                summary.AddArrangedSubview(text);
                if (line.Warning is { } warning)
                {
                    var note = Label(warning, 11);
                    note.TextColor = NSColor.SystemOrange;
                    note.PreferredMaxLayoutWidth = 560;
                    summary.AddArrangedSubview(note);
                }
            }
            sheet.Fit();
        });

        var progress = sheet.Text("", secondary: true);
        bindings.Property(editor, nameof(BulkEditViewModel.Progress), () => progress.StringValue = editor.Progress);
        var problem = sheet.Text("");
        problem.TextColor = NSColor.SystemOrange;
        bindings.Property(editor, nameof(BulkEditViewModel.Problem), () =>
        {
            problem.StringValue = editor.Problem ?? "";
            problem.Hidden = editor.Problem is null;
            sheet.Fit();
        });

        var cancel = Button("Cancel", editor.CancelCommand, bindings);
        var back = Button("Back", editor.BackCommand, bindings);
        var review = Button("Review Changes", editor.ReviewCommand, bindings);
        var apply = Button(editor.ApplyLabel, editor.ApplyCommand, bindings);
        sheet.Buttons(cancel, back, review, apply);
        bindings.Property(editor, nameof(BulkEditViewModel.Step), () =>
        {
            scroll.Hidden = !editor.IsEditingStep;
            summary.Hidden = !editor.IsSummaryStep;
            progress.Hidden = !editor.IsApplyingStep;
            back.Hidden = !editor.IsSummaryStep;
            review.Hidden = !editor.IsEditingStep;
            apply.Hidden = !editor.IsSummaryStep;
            // Return does the step's next thing, and Esc steps back before it cancels, as on Windows.
            review.KeyEquivalent = editor.IsEditingStep ? "\r" : "";
            apply.KeyEquivalent = editor.IsSummaryStep ? "\r" : "";
            cancel.KeyEquivalent = editor.IsEditingStep ? "\u001b" : "";
            back.KeyEquivalent = editor.IsSummaryStep ? "\u001b" : "";
            sheet.Fit();
        });
        sheet.Show(host);
        return (sheet, bindings);
    }

    /// <summary>Goes with: the books the cards go with now, and one to give them all, found by part of its title, or none.</summary>
    static NSStackView GoesWith(BulkGoesWithViewModel goesWith, Bindings bindings)
    {
        var current = Label(goesWith.Current, 12);
        current.TextColor = NSColor.SecondaryLabel;
        current.ToolTip = goesWith.Breakdown;
        var pending = Label("", 12);
        pending.TextColor = NSColor.ControlAccent;
        var undo = Button("Undo", goesWith.UndoCommand, bindings, inline: true);
        var choose = Button("Choose a Book…", goesWith.ChooseCommand, bindings, small: true);
        var none = Button("No Book", goesWith.NoBookCommand, bindings, small: true);
        var state = Row(current, pending, undo, new NSView(), choose, none);

        var query = new NSSearchField { PlaceholderString = "Part of the book's title" };
        query.Changed += (_, _) => goesWith.Query = query.StringValue;
        var cancel = Button("Cancel", goesWith.CancelPickingCommand, bindings, small: true);
        var search = Row(query, cancel);
        query.SetContentHuggingPriorityForOrientation(100, NSLayoutConstraintOrientation.Horizontal);
        var found = Column(2);

        var column = Column(4);
        column.AddArrangedSubview(Title("Goes with"));
        foreach (var view in new NSView[] { state, search, found })
        {
            column.AddArrangedSubview(view);
            view.WidthAnchor.ConstraintEqualTo(column.WidthAnchor).Active = true;
        }
        bindings.Properties(goesWith, [nameof(BulkGoesWithViewModel.Change), nameof(BulkGoesWithViewModel.IsPicking)], () =>
        {
            pending.StringValue = goesWith.PendingText;
            pending.Hidden = undo.Hidden = !goesWith.HasChange;
            current.Hidden = goesWith.HasChange;
            choose.Hidden = none.Hidden = goesWith.IsPicking || goesWith.HasChange;
            search.Hidden = found.Hidden = !goesWith.IsPicking;
            if (goesWith.IsPicking && query.StringValue != goesWith.Query) query.StringValue = goesWith.Query;
            if (goesWith.IsPicking) query.Window?.MakeFirstResponder(query);
            Fit(column);
        });
        bindings.Property(goesWith, nameof(BulkGoesWithViewModel.Choices), () =>
        {
            foreach (var view in found.ArrangedSubviews) view.RemoveFromSuperview();
            foreach (var choice in goesWith.Choices.Take(8))
            {
                var pick = NSButton.CreateButton($"{choice.Title}  ·  {choice.Detail}", () => goesWith.PickCommand.Execute(choice));
                pick.BezelStyle = NSBezelStyle.Inline;
                pick.LineBreakMode = NSLineBreakMode.TruncatingTail;
                found.AddArrangedSubview(pick);
            }
            Fit(column);
        });
        return column;
    }

    /// <summary>A field with one value: what the books share, or that they're mixed, and the value to give them all.</summary>
    static NSStackView Value(BulkValueFieldViewModel field, Bindings bindings)
    {
        var current = Label(field.Current, 12);
        current.TextColor = NSColor.SecondaryLabel;
        current.ToolTip = field.Breakdown;
        var pending = Label("", 12);
        pending.TextColor = NSColor.ControlAccent;
        var undo = Button("Undo", field.UndoCommand, bindings, inline: true);
        var edit = Button("Change…", field.EditCommand, bindings, small: true);
        var reset = Button("Back to Suggestions", field.ResetCommand, bindings, small: true);
        var state = Row(current, pending, undo, new NSView(), edit, reset);

        var text = new NSTextField { PlaceholderString = field.EditHint };
        text.Changed += (_, _) => field.EditText = text.StringValue;
        text.Activated += (_, _) => field.SaveCommand.Execute(null);
        text.SetContentHuggingPriorityForOrientation(100, NSLayoutConstraintOrientation.Horizontal);
        var editing = Row(text);
        if (field.UsesChoices) editing.AddArrangedSubview(Choices(field.Choices, picked => field.PickedChoice = picked));
        editing.AddArrangedSubview(Button("Cancel", field.CancelEditCommand, bindings, small: true));
        editing.AddArrangedSubview(Button("Set", field.SaveCommand, bindings, small: true));
        var hint = Hint(field, bindings);

        var column = Column(4);
        column.AddArrangedSubview(Title(field.Label));
        foreach (var view in new NSView[] { state, editing, hint })
        {
            column.AddArrangedSubview(view);
            view.WidthAnchor.ConstraintEqualTo(column.WidthAnchor).Active = true;
        }
        bindings.Properties(field, [nameof(BulkValueFieldViewModel.HasChange), nameof(BulkValueFieldViewModel.PendingText),
            nameof(BulkValueFieldViewModel.IsEditing), nameof(BulkValueFieldViewModel.ShowEdit), nameof(BulkValueFieldViewModel.ShowReset)], () =>
        {
            pending.StringValue = field.PendingText;
            pending.Hidden = undo.Hidden = !field.HasChange;
            current.Hidden = field.HasChange;
            edit.Hidden = !field.ShowEdit;
            reset.Hidden = !field.ShowReset;
            editing.Hidden = !field.IsEditing;
            if (field.IsEditing) text.Window?.MakeFirstResponder(text);
            Fit(column);
        });
        bindings.Property(field, nameof(BulkValueFieldViewModel.EditText), () =>
        {
            if (text.StringValue != field.EditText) text.StringValue = field.EditText;
        });
        return column;
    }

    /// <summary>A field with several values: a chip a value with how many books have it, and values to add to them all.</summary>
    static NSStackView Chips(BulkChipsFieldViewModel field, Bindings bindings)
    {
        var chips = new WrapView { Gap = 6 };
        var chipBindings = new Bindings();
        bindings.Add(() => chipBindings.Dispose());
        void ShowChips()
        {
            chipBindings.Dispose();
            chipBindings = new Bindings();
            var views = new List<NSView>();
            foreach (var chip in field.Chips)
            {
                var toggle = NSButton.CreateButton("", () => chip.ToggleAllCommand.Execute(null));
                toggle.ControlSize = NSControlSize.Small;
                toggle.BezelStyle = NSBezelStyle.Push;
                var remove = NSButton.CreateButton("×", () => chip.RemoveCommand.Execute(null));
                remove.ControlSize = NSControlSize.Small;
                remove.BezelStyle = NSBezelStyle.Inline;
                chipBindings.Property(chip, nameof(BulkChipViewModel.Change), () =>
                {
                    var mark = chip.IsAdding ? "+ " : chip.IsRemoving ? "− " : "";
                    toggle.Title = $"{mark}{chip.Text}  {chip.CountText}";
                    // A value new to the library says so in italics after it, as on Windows.
                    if (chip.NewNote is { } fresh)
                    {
                        var title = new NSMutableAttributedString($"{mark}{chip.Text} ");
                        var italic = NSFontManager.SharedFontManager.ConvertFont(NSFont.SystemFontOfSize(10)!, NSFontTraitMask.Italic)!;
                        title.Append(new NSAttributedString(fresh, new NSStringAttributes { Font = italic, ForegroundColor = NSColor.SecondaryLabel }.Dictionary));
                        title.Append(new NSAttributedString($"  {chip.CountText}"));
                        toggle.AttributedTitle = title;
                    }
                    toggle.ToolTip = chip.Description;
                    toggle.ContentTintColor = chip.IsAdding ? NSColor.ControlAccent : chip.IsRemoving ? NSColor.TertiaryLabel : null!;
                    remove.ToolTip = chip.RemoveName;
                    remove.Title = chip.IsRemoving ? "↺" : "×";
                });
                var pair = Row(toggle, remove);
                pair.Spacing = 0;
                views.Add(pair);
            }
            chips.SetItems(views);
        }
        bindings.Collection(field.Chips, _ => ShowChips());
        ShowChips();

        var add = new NSTextField { PlaceholderString = field.AddHint };
        add.Changed += (_, _) => field.AddText = add.StringValue;
        add.Activated += (_, _) => field.AddTypedCommand.Execute(null);
        add.SetContentHuggingPriorityForOrientation(100, NSLayoutConstraintOrientation.Horizontal);
        var adding = Row(add);
        if (field.UsesChoices) adding.AddArrangedSubview(Choices(field.Choices, picked => field.PickedChoice = picked));
        adding.AddArrangedSubview(Button(field.AddLabel, field.AddTypedCommand, bindings, small: true));
        bindings.Property(field, nameof(BulkChipsFieldViewModel.AddText), () =>
        {
            if (add.StringValue != field.AddText) add.StringValue = field.AddText;
        });
        var hint = Hint(field, bindings);

        var column = Column(4);
        column.AddArrangedSubview(Title(field.Label));
        foreach (var view in new NSView[] { chips, adding, hint })
        {
            column.AddArrangedSubview(view);
            view.WidthAnchor.ConstraintEqualTo(column.WidthAnchor).Active = true;
        }
        bindings.Property(field, nameof(BulkChipsFieldViewModel.HasChips), () => chips.Hidden = !field.HasChips);
        return column;
    }

    /// <summary>What a typed term will be filed as, and why a value can't be.</summary>
    static NSTextField Hint(BulkFieldViewModel field, Bindings bindings)
    {
        var hint = Label("", 11);
        hint.PreferredMaxLayoutWidth = 560;
        bindings.Properties(field, [nameof(BulkFieldViewModel.Problem), nameof(BulkFieldViewModel.Resolution)], () =>
        {
            hint.StringValue = field.Problem ?? field.Resolution;
            hint.TextColor = field.Problem is null ? NSColor.SecondaryLabel : NSColor.SystemOrange;
            hint.Hidden = hint.StringValue.Length == 0;
        });
        return hint;
    }

    /// <summary>The vocabulary's terms to pick from, as a pull-down beside the text.</summary>
    static NSPopUpButton Choices(IReadOnlyList<string> choices, Action<string> picked)
    {
        var popup = new NSPopUpButton(CGRect.Empty, pullsDown: true) { ControlSize = NSControlSize.Small };
        popup.Font = NSFont.SystemFontOfSize(NSFont.SmallSystemFontSize)!;
        popup.Menu!.AddItem(new NSMenuItem("Choose"));
        foreach (var choice in choices) popup.Menu.AddItem(new NSMenuItem(choice));
        popup.Activated += (_, _) =>
        {
            var at = (int)popup.IndexOfSelectedItem - 1;
            if (at >= 0 && at < choices.Count) picked(choices[at]);
        };
        return popup;
    }

    static NSButton Button(string title, System.Windows.Input.ICommand command, Bindings bindings, bool small = false, bool inline = false)
    {
        var button = NSButton.CreateButton(MacText.TitleCase(title), () => { });
        if (small || inline) button.ControlSize = NSControlSize.Small;
        if (inline) button.BezelStyle = NSBezelStyle.Inline;
        bindings.Command(button, command);
        return button;
    }

    static NSTextField Title(string text)
    {
        var label = NSTextField.CreateLabel(text);
        label.Font = NSFont.SystemFontOfSize(12, NSFontWeight.Semibold)!;
        return label;
    }

    static NSTextField Label(string text, nfloat size)
    {
        var label = NSTextField.CreateWrappingLabel(text);
        label.Font = NSFont.SystemFontOfSize(size)!;
        return label;
    }

    static NSStackView Column(nfloat spacing) => new()
    {
        Orientation = NSUserInterfaceLayoutOrientation.Vertical,
        Alignment = NSLayoutAttribute.Leading,
        Spacing = spacing,
    };

    static NSStackView Row(params NSView[] views)
    {
        var row = NSStackView.FromViews(views);
        row.Spacing = 8;
        row.Alignment = NSLayoutAttribute.CenterY;
        return row;
    }

    /// <summary>The fields scroll inside the sheet, as there are more of them than fit.</summary>
    static NSScrollView Scrolling(NSStackView content, nfloat height)
    {
        var document = new FlippedView { TranslatesAutoresizingMaskIntoConstraints = false };
        content.TranslatesAutoresizingMaskIntoConstraints = false;
        document.AddSubview(content);
        var scroll = new NSScrollView { HasVerticalScroller = true, DrawsBackground = false, DocumentView = document };
        NSLayoutConstraint.ActivateConstraints(
        [
            content.TopAnchor.ConstraintEqualTo(document.TopAnchor),
            content.LeadingAnchor.ConstraintEqualTo(document.LeadingAnchor),
            content.TrailingAnchor.ConstraintEqualTo(document.TrailingAnchor),
            content.BottomAnchor.ConstraintEqualTo(document.BottomAnchor),
            document.WidthAnchor.ConstraintEqualTo(scroll.ContentView.WidthAnchor),
            scroll.HeightAnchor.ConstraintEqualTo(height),
        ]);
        return scroll;
    }

    static void Fit(NSView view) => view.NeedsLayout = true;

    /// <summary>So the fields start at the top of the scroll view.</summary>
    sealed class FlippedView : NSView
    {
        public override bool IsFlipped => true;
    }
}
