using System.ComponentModel;
using AppKit;
using Bibliotaph.Mac.Binding;
using Bibliotaph.Presentation.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Bibliotaph.Mac.Views;

/// <summary>
/// Settings › Vocabulary, as on Windows: a vocabulary's terms with the other names each goes by, edited in place, and
/// adding to it; what the AI may add; finding terms that overlap; and the user's own tags with the words that find them.
/// </summary>
public sealed partial class SettingsWindow
{
    NSScrollView VocabularyTab()
    {
        var vocabulary = _services.GetRequiredService<VocabularyViewModel>();
        var bindings = Load(vocabulary);

        var intro = Secondary("The game systems, types and other terms your books are filed under, and the other names each one goes by. Folder names, searches and what you type all go through these names.");
        var choose = new NSPopUpButton();
        foreach (var choice in vocabulary.Vocabularies) choose.Menu!.AddItem(new NSMenuItem(choice.Label));
        choose.Activated += (_, _) =>
        {
            if (choose.IndexOfSelectedItem is var at and >= 0 && at < vocabulary.Vocabularies.Count) vocabulary.Vocabularies[(int)at].IsSelected = true;
        };
        foreach (var choice in vocabulary.Vocabularies)
            bindings.Property(choice, nameof(VocabularyChoice.IsSelected), () =>
            {
                if (choice.IsSelected) choose.SelectItem(vocabulary.Vocabularies.ToList().IndexOf(choice));
            });

        var terms = TermsPart(vocabulary, bindings);
        var tags = TagsPart(vocabulary, bindings);
        bindings.Property(vocabulary, nameof(VocabularyViewModel.IsTags), () =>
        {
            terms.Hidden = vocabulary.IsTags;
            tags.Hidden = !vocabulary.IsTags;
        });
        var content = Stack(vertical: true, spacing: 14, intro, choose, terms, tags);
        terms.WidthAnchor.ConstraintEqualTo(content.WidthAnchor, 1, -48).Active = true;
        tags.WidthAnchor.ConstraintEqualTo(content.WidthAnchor, 1, -48).Active = true;
        return Scrolling(content);
    }

    /// <summary>A vocabulary's terms: what the AI may add, Find overlaps, adding a term, and the list, filtered.</summary>
    static NSStackView TermsPart(VocabularyViewModel vocabulary, Bindings bindings)
    {
        var aiTitle = Section(VocabularyViewModel.AiTermsTitle);
        var ask = NSButton.CreateRadioButton("Ask first", () => vocabulary.AiTermsAsk = true);
        var add = NSButton.CreateRadioButton("Add them", () => vocabulary.AiTermsAdd = true);
        var aiNote = Secondary("");
        var ai = Stack(vertical: true, spacing: 4, aiTitle, Secondary(VocabularyViewModel.AiTermsHelp), Stack(vertical: false, spacing: 12, ask, add), aiNote);
        bindings.Properties(vocabulary, [nameof(VocabularyViewModel.OffersAiTerms), nameof(VocabularyViewModel.AiTermsAsk),
            nameof(VocabularyViewModel.AiTermsAdd), nameof(VocabularyViewModel.AiTermsNote)], () =>
        {
            ai.Hidden = !vocabulary.OffersAiTerms;
            ask.State = vocabulary.AiTermsAsk ? NSCellStateValue.On : NSCellStateValue.Off;
            add.State = vocabulary.AiTermsAdd ? NSCellStateValue.On : NSCellStateValue.Off;
            Show(aiNote, vocabulary.AiTermsNote);
        });

        var find = Button(VocabularyViewModel.OverlapsTitle, vocabulary.FindOverlapsCommand, bindings);
        var comparing = Secondary(VocabularyViewModel.OverlapsComparing);
        var overlapsNote = Secondary("");
        var openReview = Button(VocabularyViewModel.OpenNeedsReviewLabel, vocabulary.OpenNeedsReviewCommand, bindings);
        var aiComparing = Secondary(VocabularyViewModel.OverlapsAiComparing);
        var keptNote = Section("");
        var kept = Stack(vertical: true, spacing: 4);
        bindings.Properties(vocabulary, [nameof(VocabularyViewModel.IsFindingOverlaps), nameof(VocabularyViewModel.OverlapsNote),
            nameof(VocabularyViewModel.HasOverlapsWaiting), nameof(VocabularyViewModel.IsAiComparing), nameof(VocabularyViewModel.KeptApartNote)], () =>
        {
            comparing.Hidden = !vocabulary.IsFindingOverlaps;
            Show(overlapsNote, vocabulary.OverlapsNote);
            openReview.Hidden = !vocabulary.HasOverlapsWaiting;
            aiComparing.Hidden = !vocabulary.IsAiComparing;
            keptNote.StringValue = vocabulary.KeptApartNote ?? "";
            keptNote.Hidden = vocabulary.KeptApartNote is null;
        });
        var keptBindings = new Bindings();
        bindings.Add(() => keptBindings.Dispose());
        bindings.Collection(vocabulary.KeptApart, _ =>
        {
            keptBindings.Dispose();
            keptBindings = new Bindings();
            foreach (var view in kept.ArrangedSubviews) view.RemoveFromSuperview();
            foreach (var pair in vocabulary.KeptApart)
                kept.AddArrangedSubview(Stack(vertical: false, spacing: 8, NSTextField.CreateLabel(pair.Text), Small(VocabularyViewModel.ForgetKeptApart, pair.ForgetCommand, keptBindings)));
        });
        var overlaps = Stack(vertical: true, spacing: 4, Section(VocabularyViewModel.OverlapsTitle), Secondary(VocabularyViewModel.OverlapsHelp), find,
            comparing, overlapsNote, openReview, aiComparing, keptNote, kept);

        var addLabel = Section("");
        bindings.Property(vocabulary, nameof(VocabularyViewModel.AddLabel), () => addLabel.StringValue = vocabulary.AddLabel);
        var newTerm = TextField("", vocabulary, nameof(VocabularyViewModel.NewTerm), () => vocabulary.NewTerm, v => vocabulary.NewTerm = v, bindings);
        newTerm.Activated += (_, _) => vocabulary.AddTermCommand.Execute(null);
        var addTerm = Button("Add", vocabulary.AddTermCommand, bindings);
        var addProblem = Secondary("");
        addProblem.TextColor = NSColor.SystemOrange;
        var addNote = Secondary("");
        bindings.Properties(vocabulary, [nameof(VocabularyViewModel.AddProblem), nameof(VocabularyViewModel.AddNote)], () =>
        {
            Show(addProblem, vocabulary.AddProblem);
            Show(addNote, vocabulary.AddNote);
        });
        var adding = Stack(vertical: true, spacing: 4, addLabel, Stack(vertical: false, spacing: 8, newTerm, addTerm), addProblem, addNote);
        newTerm.WidthAnchor.ConstraintEqualTo(280).Active = true;

        var filter = new NSSearchField { PlaceholderString = "Find a term" };
        filter.Changed += (_, _) => vocabulary.Filter = filter.StringValue;
        filter.WidthAnchor.ConstraintEqualTo(280).Active = true;
        var list = Stack(vertical: true, spacing: 8);
        var rowBindings = new Bindings();
        bindings.Add(() => rowBindings.Dispose());
        void ShowTerms()
        {
            rowBindings.Dispose();
            rowBindings = new Bindings();
            foreach (var view in list.ArrangedSubviews) view.RemoveFromSuperview();
            foreach (var term in vocabulary.Terms)
            {
                var row = TermRow(term, rowBindings);
                list.AddArrangedSubview(row);
                row.WidthAnchor.ConstraintEqualTo(list.WidthAnchor).Active = true;
            }
        }
        bindings.Collection(vocabulary.Terms, _ => ShowTerms());
        ShowTerms();

        var part = Stack(vertical: true, spacing: 16, ai, overlaps, adding, filter, list,
            Secondary("Renaming keeps everything filed under a term. Changes reach the library in the background; books under renamed folders are read again."));
        list.WidthAnchor.ConstraintEqualTo(part.WidthAnchor).Active = true;
        return part;
    }

    /// <summary>A term and its other names; Edit opens its names in place.</summary>
    static NSStackView TermRow(TermRowViewModel term, Bindings bindings)
    {
        var row = Stack(vertical: true, spacing: 6);
        var own = new Bindings();
        bindings.Add(() => own.Dispose());
        bindings.Property(term, nameof(TermRowViewModel.IsEditing), () =>
        {
            own.Dispose();
            own = new Bindings();
            foreach (var view in row.ArrangedSubviews) view.RemoveFromSuperview();
            if (!term.IsEditing)
            {
                var label = NSTextField.CreateLabel(term.Label);
                label.Font = NSFont.SystemFontOfSize(13, NSFontWeight.Medium)!;
                var detail = Secondary(term.Detail);
                detail.Font = NSFont.SystemFontOfSize(11)!;
                detail.Hidden = term.Detail.Length == 0;
                var text = Stack(vertical: true, spacing: 1, label, detail);
                text.SetContentHuggingPriorityForOrientation(200, NSLayoutConstraintOrientation.Horizontal);
                // The name takes the width, and Edit stays at the end of the row.
                var line = Stack(vertical: false, spacing: 8, text, Small("Edit", term.EditCommand, own));
                line.Distribution = NSStackViewDistribution.Fill;
                row.AddArrangedSubview(line);
                line.WidthAnchor.ConstraintEqualTo(row.WidthAnchor).Active = true;
                return;
            }
            var name = TextField("Name", term, nameof(TermRowViewModel.EditLabel), () => term.EditLabel, v => term.EditLabel = v, own);
            name.Activated += (_, _) => term.RenameCommand.Execute(null);
            var shortName = TextField("Short name, on covers", term, nameof(TermRowViewModel.EditShort), () => term.EditShort, v => term.EditShort = v, own);
            shortName.Activated += (_, _) => term.RenameCommand.Execute(null);
            // The other names wrap, as there can be many.
            var aliases = new WrapView { Gap = 6 };
            aliases.SetItems(term.HasAliases
                ? term.Aliases.Select(alias => (NSView)Stack(vertical: false, spacing: 0, NSTextField.CreateLabel(alias.Text), Small("×", term.RemoveAliasCommand, own, alias)))
                : [NSTextField.CreateLabel("None yet.")]);
            var newAlias = TextField("Another name", term, nameof(TermRowViewModel.NewAlias), () => term.NewAlias, v => term.NewAlias = v, own);
            newAlias.Activated += (_, _) => term.AddAliasCommand.Execute(null);
            var problem = Secondary("");
            problem.TextColor = NSColor.SystemOrange;
            var note = Secondary("");
            own.Properties(term, [nameof(TermRowViewModel.Problem), nameof(TermRowViewModel.Note)], () =>
            {
                Show(problem, term.Problem);
                Show(note, term.Note);
            });
            foreach (var field in new[] { name, shortName, newAlias }) field.WidthAnchor.ConstraintEqualTo(260).Active = true;
            foreach (var view in new NSView[]
            {
                Stack(vertical: false, spacing: 8, Section("Name"), name),
                Stack(vertical: false, spacing: 8, Section("Short name, on covers"), shortName, Small("Save name", term.RenameCommand, own)),
                Section("Other names"), aliases,
                Stack(vertical: false, spacing: 8, newAlias, Small("Add name", term.AddAliasCommand, own)),
                Secondary("A name another term already has as another name moves here."),
                problem, note, Small("Done", term.CloseCommand, own),
            }) row.AddArrangedSubview(view);
            aliases.WidthAnchor.ConstraintEqualTo(row.WidthAnchor).Active = true;
            row.Window?.MakeFirstResponder(name);
        });
        return row;
    }

    /// <summary>The user's own tags: a new tag and its words, and each tag, edited in place.</summary>
    static NSStackView TagsPart(VocabularyViewModel vocabulary, Bindings bindings)
    {
        var help = Secondary("Your own tags, found in your books by the words on their pages. A book whose pages use a tag's words often enough gets the tag, marked Suggested until you keep it. A tag you reject on a book never comes back to it.");
        var name = TextField("New tag", vocabulary, nameof(VocabularyViewModel.NewTagName), () => vocabulary.NewTagName, v => vocabulary.NewTagName = v, bindings);
        var words = TextField("Words that signal it, separated by commas", vocabulary, nameof(VocabularyViewModel.NewTagWords),
            () => vocabulary.NewTagWords, v => vocabulary.NewTagWords = v, bindings);
        name.Activated += (_, _) => vocabulary.AddTagCommand.Execute(null);
        words.Activated += (_, _) => vocabulary.AddTagCommand.Execute(null);
        name.WidthAnchor.ConstraintEqualTo(200).Active = true;
        words.WidthAnchor.ConstraintEqualTo(320).Active = true;
        var searching = Secondary("Looking through your books…");
        var problem = Secondary("");
        problem.TextColor = NSColor.SystemOrange;
        var note = Secondary("");
        bindings.Properties(vocabulary, [nameof(VocabularyViewModel.IsSearching), nameof(VocabularyViewModel.AddProblem), nameof(VocabularyViewModel.AddNote)], () =>
        {
            searching.Hidden = !vocabulary.IsSearching;
            Show(problem, vocabulary.AddProblem);
            Show(note, vocabulary.AddNote);
        });
        var list = Stack(vertical: true, spacing: 4);
        var rowBindings = new Bindings();
        bindings.Add(() => rowBindings.Dispose());
        void ShowTags()
        {
            rowBindings.Dispose();
            rowBindings = new Bindings();
            foreach (var view in list.ArrangedSubviews) view.RemoveFromSuperview();
            foreach (var tag in vocabulary.Tags)
            {
                var row = TagRow(tag, rowBindings);
                list.AddArrangedSubview(row);
                row.WidthAnchor.ConstraintEqualTo(list.WidthAnchor).Active = true;
            }
        }
        bindings.Collection(vocabulary.Tags, _ => ShowTags());
        ShowTags();
        var part = Stack(vertical: true, spacing: 10, help, Secondary(VocabularyViewModel.TagRuleHelp),
            Stack(vertical: false, spacing: 8, name, words, Button("Add tag", vocabulary.AddTagCommand, bindings)), searching, problem, note, list);
        list.WidthAnchor.ConstraintEqualTo(part.WidthAnchor).Active = true;
        return part;
    }

    static NSStackView TagRow(UserTagRowViewModel tag, Bindings bindings)
    {
        var row = Stack(vertical: true, spacing: 6);
        var own = new Bindings();
        bindings.Add(() => own.Dispose());
        bindings.Property(tag, nameof(UserTagRowViewModel.IsEditing), () =>
        {
            own.Dispose();
            own = new Bindings();
            foreach (var view in row.ArrangedSubviews) view.RemoveFromSuperview();
            if (!tag.IsEditing)
            {
                var label = NSTextField.CreateLabel(tag.Name);
                label.Font = NSFont.SystemFontOfSize(13, NSFontWeight.Medium)!;
                var detail = Secondary(tag.Detail);
                detail.Font = NSFont.SystemFontOfSize(11)!;
                var text = Stack(vertical: true, spacing: 1, label, detail);
                text.SetContentHuggingPriorityForOrientation(200, NSLayoutConstraintOrientation.Horizontal);
                // The name takes the width, and Edit stays at the end of the row.
                var line = Stack(vertical: false, spacing: 8, text, Small("Edit", tag.EditCommand, own));
                line.Distribution = NSStackViewDistribution.Fill;
                row.AddArrangedSubview(line);
                line.WidthAnchor.ConstraintEqualTo(row.WidthAnchor).Active = true;
                return;
            }
            var name = TextField("Name", tag, nameof(UserTagRowViewModel.EditName), () => tag.EditName, v => tag.EditName = v, own);
            var words = TextField("Words that signal it, separated by commas", tag, nameof(UserTagRowViewModel.EditWords), () => tag.EditWords, v => tag.EditWords = v, own);
            name.Activated += (_, _) => tag.SaveCommand.Execute(null);
            words.Activated += (_, _) => tag.SaveCommand.Execute(null);
            name.WidthAnchor.ConstraintEqualTo(260).Active = true;
            words.WidthAnchor.ConstraintEqualTo(360).Active = true;
            var problem = Secondary("");
            problem.TextColor = NSColor.SystemOrange;
            var note = Secondary("");
            own.Properties(tag, [nameof(UserTagRowViewModel.Problem), nameof(UserTagRowViewModel.Note)], () =>
            {
                Show(problem, tag.Problem);
                Show(note, tag.Note);
            });
            foreach (var view in new NSView[]
            {
                Stack(vertical: false, spacing: 8, Section("Name"), name),
                Stack(vertical: false, spacing: 8, Section("Words"), words),
                problem, note,
                Stack(vertical: false, spacing: 8, Small("Save", tag.SaveCommand, own), Small("Delete tag", tag.DeleteCommand, own), Small("Done", tag.CloseCommand, own)),
            }) row.AddArrangedSubview(view);
        });
        return row;
    }

    /// <summary>A text field over a view model's text, which follows it unless it is what was just typed.</summary>
    static NSTextField TextField(string placeholder, INotifyPropertyChanged source, string property, Func<string> get, Action<string> set, Bindings bindings)
    {
        var field = new NSTextField { PlaceholderString = placeholder };
        field.Changed += (_, _) => set(field.StringValue);
        bindings.Property(source, property, () =>
        {
            if (field.StringValue != get()) field.StringValue = get();
        });
        return field;
    }

    static NSButton Small(string title, System.Windows.Input.ICommand command, Bindings bindings, object? parameter = null)
    {
        var button = NSButton.CreateButton(title, () => { });
        button.ControlSize = NSControlSize.Small;
        bindings.Command(button, command, parameter is null ? null : () => parameter);
        return button;
    }

    static void Show(NSTextField label, string? text)
    {
        label.StringValue = text ?? "";
        label.Hidden = string.IsNullOrEmpty(text);
    }
}
