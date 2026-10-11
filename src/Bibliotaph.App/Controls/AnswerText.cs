using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using Bibliotaph.Classification;

namespace Bibliotaph.App.Controls;

/// <summary>
/// Shows an Ask answer, as checked, in a TextBlock (Ask plan, step 5): each citation a link that runs
/// <see cref="CommandProperty"/> with its <see cref="AnswerPart"/>, a quotation that wasn't on its page in italics and
/// without its marks, and a sentence whose citations were removed followed by a muted note saying so.
/// </summary>
public static class AnswerText
{
    public static readonly DependencyProperty PartsProperty = DependencyProperty.RegisterAttached(
        "Parts", typeof(IReadOnlyList<AnswerPart>), typeof(AnswerText), new PropertyMetadata(null, OnChanged));

    public static IReadOnlyList<AnswerPart>? GetParts(DependencyObject element) => (IReadOnlyList<AnswerPart>?)element.GetValue(PartsProperty);
    public static void SetParts(DependencyObject element, IReadOnlyList<AnswerPart>? value) => element.SetValue(PartsProperty, value);

    /// <summary>What a citation's link runs, with the citation as its parameter.</summary>
    public static readonly DependencyProperty CommandProperty = DependencyProperty.RegisterAttached(
        "Command", typeof(ICommand), typeof(AnswerText), new PropertyMetadata(null, OnChanged));

    public static ICommand? GetCommand(DependencyObject element) => (ICommand?)element.GetValue(CommandProperty);
    public static void SetCommand(DependencyObject element, ICommand? value) => element.SetValue(CommandProperty, value);

    static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock block) return;
        block.Inlines.Clear();
        var command = GetCommand(block);
        foreach (var part in GetParts(block) ?? [])
        {
            switch (part.Kind)
            {
                case AnswerPartKind.Citation:
                    var link = new Hyperlink(new Run(part.Text)) { Command = command, CommandParameter = part, ToolTip = "Open this page" };
                    link.SetResourceReference(TextElement.ForegroundProperty, "Bt.Accent");
                    AutomationProperties.SetName(link, $"Go to page {part.Text.Trim('[', ']').Replace("p. ", "", StringComparison.Ordinal)}");
                    block.Inlines.Add(link);
                    break;
                case AnswerPartKind.Paraphrase:
                    var paraphrase = new Run(part.Text) { FontStyle = FontStyles.Italic, ToolTip = "Not the book's exact words: this quotation isn't on the page it cites." };
                    block.Inlines.Add(paraphrase);
                    break;
                case AnswerPartKind.Unsupported:
                    var note = new Run(" " + part.Text) { FontStyle = FontStyles.Italic, ToolTip = "This cited a page Ask didn't read, so the citation was taken out." };
                    note.SetResourceReference(TextElement.ForegroundProperty, "Bt.Muted");
                    block.Inlines.Add(note);
                    break;
                default:
                    block.Inlines.Add(new Run(part.Text));
                    break;
            }
        }
    }
}
