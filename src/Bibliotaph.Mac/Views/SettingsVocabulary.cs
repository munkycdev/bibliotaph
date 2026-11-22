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
        var form = new Form();

        var lists = form.Section(null,
            "The game systems, types and other terms your books are filed under, and the other names each one goes by. Folder names, searches and what you type all go through these names.");
        var choose = FormText.Choices(vocabulary.Vocabularies.Select(v => v.Label), at =>
        {
            if (at >= 0 && at < vocabulary.Vocabularies.Count) vocabulary.Vocabularies[at].IsSelected = true;
        });
        lists.Row("Vocabulary", choose);
        foreach (var choice in vocabulary.Vocabularies)
            bindings.Property(choice, nameof(VocabularyChoice.IsSelected), () =>
            {
                if (choice.IsSelected) choose.SelectItem(vocabulary.Vocabularies.ToList().IndexOf(choice));
            });

        var terms = TermsPart(form, vocabulary, bindings);
        var tags = TagsPart(form, vocabulary, bindings);
        bindings.Property(vocabulary, nameof(VocabularyViewModel.IsTags), () =>
        {
            foreach (var view in terms) view.Hidden = vocabulary.IsTags;
            foreach (var view in tags) view.Hidden = !vocabulary.IsTags;
        });
        return Pane(form);
    }

    /// <summary>A vocabulary's sections: what the AI may add, Find overlaps, adding a term, and its terms, filtered.</summary>
    static List<NSView> TermsPart(Form form, VocabularyViewModel vocabulary, Bindings bindings)
    {
        var ai = form.Section(null, VocabularyViewModel.AiTermsHelp);
        var aiChoice = FormText.Choices(["Ask first", "Add them"], at =>
        {
            if (at == 0) vocabulary.AiTermsAsk = true;
            else vocabulary.AiTermsAdd = true;
        });
        var aiRow = ai.Row(VocabularyViewModel.AiTermsTitle, aiChoice);
        bindings.Properties(vocabulary, [nameof(VocabularyViewModel.OffersAiTerms), nameof(VocabularyViewModel.IsTags), nameof(VocabularyViewModel.AiTermsAsk),
            nameof(VocabularyViewModel.AiTermsAdd), nameof(VocabularyViewModel.AiTermsNote)], () =>
        {
            ai.View.Hidden = !vocabulary.OffersAiTerms || vocabulary.IsTags;
            aiChoice.SelectItem(vocabulary.AiTermsAdd ? 1 : 0);
            aiRow.ShowDetail(vocabulary.AiTermsNote);
        });

        var overlaps = form.Section(null, VocabularyViewModel.OverlapsHelp);
        var find = overlaps.Row(VocabularyViewModel.OverlapsTitle, Button("Find", vocabulary.FindOverlapsCommand, bindings));
        var review = overlaps.Row("", Button(VocabularyViewModel.OpenNeedsReviewLabel, vocabulary.OpenNeedsReviewCommand, bindings));
        bindings.Properties(vocabulary, [nameof(VocabularyViewModel.IsFindingOverlaps), nameof(VocabularyViewModel.OverlapsNote),
            nameof(VocabularyViewModel.HasOverlapsWaiting), nameof(VocabularyViewModel.IsAiComparing)], () =>
        {
            find.ShowDetail(vocabulary.IsFindingOverlaps ? VocabularyViewModel.OverlapsComparing
                : vocabulary.IsAiComparing ? VocabularyViewModel.OverlapsAiComparing : null);
            review.Label.StringValue = vocabulary.OverlapsNote ?? "";
            review.Hidden = vocabulary.OverlapsNote is null;
            review.Controls.Hidden = !vocabulary.HasOverlapsWaiting;
        });
        var kept = form.Section("");
        var keptBindings = new Bindings();
        bindings.Add(() => keptBindings.Dispose());
        void ShowKept()
        {
            keptBindings.Dispose();
            keptBindings = new Bindings();
            kept.Clear();
            kept.Title.StringValue = vocabulary.KeptApartNote ?? "";
            foreach (var pair in vocabulary.KeptApart) kept.Row(pair.Text, Small(VocabularyViewModel.ForgetKeptApart, pair.ForgetCommand, keptBindings));
            kept.View.Hidden = vocabulary.KeptApartNote is null || vocabulary.IsTags;
        }
        bindings.Collection(vocabulary.KeptApart, _ => ShowKept());
        bindings.Properties(vocabulary, [nameof(VocabularyViewModel.KeptApartNote), nameof(VocabularyViewModel.IsTags)], ShowKept);

        var adding = form.Section();
        var newTerm = TextField("A new name", vocabulary, nameof(VocabularyViewModel.NewTerm), () => vocabulary.NewTerm, v => vocabulary.NewTerm = v, bindings);
        newTerm.Activated += (_, _) => vocabulary.AddTermCommand.Execute(null);
        newTerm.WidthAnchor.ConstraintEqualTo(220).Active = true;
        var add = adding.Row("", newTerm, Button("Add", vocabulary.AddTermCommand, bindings));
        bindings.Property(vocabulary, nameof(VocabularyViewModel.AddLabel), () => add.Label.StringValue = vocabulary.AddLabel);
        bindings.Properties(vocabulary, [nameof(VocabularyViewModel.AddProblem), nameof(VocabularyViewModel.AddNote)], () =>
        {
            add.ShowDetail(vocabulary.AddProblem ?? vocabulary.AddNote);
            add.Detail.TextColor = vocabulary.AddProblem is null ? NSColor.SecondaryLabel : NSColor.SystemOrange;
        });

        var filter = new NSSearchField { PlaceholderString = "Find a term" };
        filter.Changed += (_, _) => vocabulary.Filter = filter.StringValue;
        form.Add(filter);
        var list = form.Section(null,
            "Renaming keeps everything filed under a term. Changes reach the library in the background; books under renamed folders are read again.");
        var rowBindings = new Bindings();
        bindings.Add(() => rowBindings.Dispose());
        void ShowTerms()
        {
            rowBindings.Dispose();
            rowBindings = new Bindings();
            list.Clear();
            foreach (var term in vocabulary.Terms)
            {
                // A term being edited opens in its row; any other row shows again as it closes.
                term.PropertyChanged += Changed;
                rowBindings.Add(() => term.PropertyChanged -= Changed);
                if (term.IsEditing) list.Wide(TermEditor(term, rowBindings));
                else list.Row(term.Label, term.Detail.Length == 0 ? null : term.Detail, Small("Edit", term.EditCommand, rowBindings));
            }
            void Changed(object? sender, PropertyChangedEventArgs e)
            {
                if (e.PropertyName == nameof(TermRowViewModel.IsEditing)) Bindings.OnMain(ShowTerms);
            }
        }
        bindings.Collection(vocabulary.Terms, _ => ShowTerms());
        ShowTerms();
        // What the AI may add and the pairs kept apart show and hide themselves.
        return [overlaps.View, adding.View, filter, list.View];
    }

    /// <summary>A term's names opened for editing: its name and short name, its other names, and adding one.</summary>
    static NSStackView TermEditor(TermRowViewModel term, Bindings bindings)
    {
        var name = TextField("Name", term, nameof(TermRowViewModel.EditLabel), () => term.EditLabel, v => term.EditLabel = v, bindings);
        name.Activated += (_, _) => term.RenameCommand.Execute(null);
        var shortName = TextField("Short name, on covers", term, nameof(TermRowViewModel.EditShort), () => term.EditShort, v => term.EditShort = v, bindings);
        shortName.Activated += (_, _) => term.RenameCommand.Execute(null);
        // The other names wrap, as there can be many.
        var aliases = new WrapView { Gap = 6 };
        aliases.SetItems(term.HasAliases
            ? term.Aliases.Select(alias => (NSView)Stack(vertical: false, spacing: 0, NSTextField.CreateLabel(alias.Text), Small("×", term.RemoveAliasCommand, bindings, alias)))
            : [FormText.Note("None yet.", 12)]);
        var newAlias = TextField("Another name", term, nameof(TermRowViewModel.NewAlias), () => term.NewAlias, v => term.NewAlias = v, bindings);
        newAlias.Activated += (_, _) => term.AddAliasCommand.Execute(null);
        var note = FormText.Note("A name another term already has as another name moves here.", 11);
        bindings.Properties(term, [nameof(TermRowViewModel.Problem), nameof(TermRowViewModel.Note)], () =>
        {
            note.StringValue = term.Problem ?? term.Note ?? "A name another term already has as another name moves here.";
            note.TextColor = term.Problem is null ? NSColor.SecondaryLabel : NSColor.SystemOrange;
        });
        foreach (var field in new[] { name, shortName, newAlias }) field.WidthAnchor.ConstraintEqualTo(240).Active = true;
        var done = Small("Done", term.CloseCommand, bindings);
        done.KeyEquivalent = "\u001b";
        var editor = Stack(vertical: true, spacing: 8,
            Stack(vertical: false, spacing: 8, name, shortName, Small("Save Name", term.RenameCommand, bindings)),
            FormText.Note("Other names", 11), aliases,
            Stack(vertical: false, spacing: 8, newAlias, Small("Add Name", term.AddAliasCommand, bindings)),
            note, done);
        aliases.WidthAnchor.ConstraintEqualTo(editor.WidthAnchor).Active = true;
        return editor;
    }

    /// <summary>The user's own tags: a new tag and its words, and each tag, edited in place.</summary>
    static List<NSView> TagsPart(Form form, VocabularyViewModel vocabulary, Bindings bindings)
    {
        var adding = form.Section("Your tags",
            "Found in your books by the words on their pages. A book whose pages use a tag's words often enough gets the tag, marked Suggested until you keep it. A tag you reject on a book never comes back to it. " + VocabularyViewModel.TagRuleHelp);
        var name = TextField("Name", vocabulary, nameof(VocabularyViewModel.NewTagName), () => vocabulary.NewTagName, v => vocabulary.NewTagName = v, bindings);
        var words = TextField("Words that signal it, separated by commas", vocabulary, nameof(VocabularyViewModel.NewTagWords),
            () => vocabulary.NewTagWords, v => vocabulary.NewTagWords = v, bindings);
        name.Activated += (_, _) => vocabulary.AddTagCommand.Execute(null);
        words.Activated += (_, _) => vocabulary.AddTagCommand.Execute(null);
        name.WidthAnchor.ConstraintEqualTo(140).Active = true;
        words.WidthAnchor.ConstraintEqualTo(240).Active = true;
        var add = adding.Row("New tag", name, words, Button("Add", vocabulary.AddTagCommand, bindings));
        bindings.Properties(vocabulary, [nameof(VocabularyViewModel.IsSearching), nameof(VocabularyViewModel.AddProblem), nameof(VocabularyViewModel.AddNote)], () =>
        {
            add.ShowDetail(vocabulary.IsSearching ? "Looking through your books…" : vocabulary.AddProblem ?? vocabulary.AddNote);
            add.Detail.TextColor = vocabulary.AddProblem is null ? NSColor.SecondaryLabel : NSColor.SystemOrange;
        });

        var list = form.Section();
        var rowBindings = new Bindings();
        bindings.Add(() => rowBindings.Dispose());
        void ShowTags()
        {
            rowBindings.Dispose();
            rowBindings = new Bindings();
            list.Clear();
            foreach (var tag in vocabulary.Tags)
            {
                tag.PropertyChanged += Changed;
                rowBindings.Add(() => tag.PropertyChanged -= Changed);
                if (tag.IsEditing) list.Wide(TagEditor(tag, rowBindings));
                else list.Row(tag.Name, tag.Detail, Small("Edit", tag.EditCommand, rowBindings));
            }
            list.View.Hidden = vocabulary.Tags.Count == 0;
            void Changed(object? sender, PropertyChangedEventArgs e)
            {
                if (e.PropertyName == nameof(UserTagRowViewModel.IsEditing)) Bindings.OnMain(ShowTags);
            }
        }
        bindings.Collection(vocabulary.Tags, _ => ShowTags());
        ShowTags();
        return [adding.View, list.View];
    }

    static NSStackView TagEditor(UserTagRowViewModel tag, Bindings bindings)
    {
        var name = TextField("Name", tag, nameof(UserTagRowViewModel.EditName), () => tag.EditName, v => tag.EditName = v, bindings);
        var words = TextField("Words that signal it, separated by commas", tag, nameof(UserTagRowViewModel.EditWords), () => tag.EditWords, v => tag.EditWords = v, bindings);
        name.Activated += (_, _) => tag.SaveCommand.Execute(null);
        words.Activated += (_, _) => tag.SaveCommand.Execute(null);
        name.WidthAnchor.ConstraintEqualTo(160).Active = true;
        words.WidthAnchor.ConstraintEqualTo(300).Active = true;
        var note = FormText.Note("", 11);
        bindings.Properties(tag, [nameof(UserTagRowViewModel.Problem), nameof(UserTagRowViewModel.Note)], () =>
        {
            note.StringValue = tag.Problem ?? tag.Note ?? "";
            note.Hidden = note.StringValue.Length == 0;
            note.TextColor = tag.Problem is null ? NSColor.SecondaryLabel : NSColor.SystemOrange;
        });
        return Stack(vertical: true, spacing: 8,
            Stack(vertical: false, spacing: 8, name, words),
            note,
            Stack(vertical: false, spacing: 8, Small("Save", tag.SaveCommand, bindings), Small("Delete Tag", tag.DeleteCommand, bindings), Small("Done", tag.CloseCommand, bindings)));
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
}
