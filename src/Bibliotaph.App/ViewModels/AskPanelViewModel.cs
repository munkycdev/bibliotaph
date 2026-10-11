using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Threading;
using Bibliotaph.Processing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.App.ViewModels;

/// <summary>A page shown under a question in the Ask panel: "p. 112 · The Mill", a few of its lines, and how it was found.</summary>
public sealed class AskPassageRow(AskPassage passage)
{
    public AskPassage Passage { get; } = passage;

    /// <summary>"p. 112 · The Mill", or "p. 112" for a page under no bookmark.</summary>
    public string Link => Passage.Heading is { } heading ? $"p. {Passage.Page} · {heading}" : $"p. {Passage.Page}";

    public string Text => Passage.Text;

    /// <summary>"Found by its words", "by its meaning", or both.</summary>
    public string Found => (Passage.ByWords, Passage.ByMeaning) switch
    {
        (true, true) => "Found by its words and its meaning",
        (false, true) => "Found by its meaning",
        _ => "Found by its words",
    };

    public string AccessibleName => $"Go to page {Passage.Page}";
}

/// <summary>
/// The reader's Ask panel (Ask this book, Q1): a question about the open book, and the pages that look like they answer
/// it, each a link that opens the page with the matching word marked by Find, with how much of the book has text and
/// whether pages were found by meaning as well as by words. Shown only while AI is on (choice 12).
/// </summary>
public sealed partial class AskPanelViewModel : ObservableObject
{
    readonly AskService _ask;
    readonly Dispatcher _dispatcher;
    readonly ILogger _log;
    readonly Func<int, string?, Task> _show;
    long? _documentId;
    int _version;

    /// <param name="ask">Finds the pages.</param>
    /// <param name="dispatcher">The reader's, for changes raised on other threads.</param>
    /// <param name="log">Failures; never the question or the book's text.</param>
    /// <param name="show">Shows a PDF page with a word marked on it.</param>
    public AskPanelViewModel(AskService ask, Dispatcher dispatcher, ILogger log, Func<int, string?, Task> show)
    {
        _ask = ask;
        _dispatcher = dispatcher;
        _log = log;
        _show = show;
        IsOn = ask.IsOn;
    }

    /// <summary>AI is on, so the book can be asked; with AI off the panel and its button are hidden.</summary>
    [ObservableProperty]
    public partial bool IsOn { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AskCommand))]
    public partial string Question { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AskCommand))]
    public partial bool IsAsking { get; private set; }

    /// <summary>The question the results are for, as asked.</summary>
    [ObservableProperty]
    public partial string? Asked { get; private set; }

    public ObservableCollection<AskPassageRow> Passages { get; } = [];

    [ObservableProperty]
    public partial bool HasPassages { get; private set; }

    /// <summary>Why there are no pages: nothing looks like it's about the question, or the book can't be asked.</summary>
    [ObservableProperty]
    public partial string? Message { get; private set; }

    /// <summary>"212 of 226 pages have text; 14 await OCR."</summary>
    [ObservableProperty]
    public partial string? CoverageText { get; private set; }

    /// <summary>Whether pages were found by meaning too, and how far the book's vectors have got.</summary>
    [ObservableProperty]
    public partial string? MeaningText { get; private set; }

    AskFindings? _findings;

    /// <summary>The reader opened a PDF: questions are about it from now on.</summary>
    public void Attach(long documentId)
    {
        _ask.Changed -= OnAiChanged;
        _ask.Changed += OnAiChanged;
        _ask.FillChanged -= OnFillChanged;
        _ask.FillChanged += OnFillChanged;
        IsOn = _ask.IsOn;
        if (_documentId == documentId) return;
        _documentId = documentId;
        Clear();
        AskCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The reader closed the book.</summary>
    public void Detach()
    {
        _ask.Changed -= OnAiChanged;
        _ask.FillChanged -= OnFillChanged;
        _documentId = null;
        _version++;
        IsAsking = false;
        AskCommand.NotifyCanExecuteChanged();
    }

    void Clear()
    {
        _version++;
        _findings = null;
        Asked = null;
        Passages.Clear();
        HasPassages = false;
        Message = null;
        CoverageText = null;
        MeaningText = null;
        IsAsking = false;
    }

    void OnAiChanged(object? sender, EventArgs e) => _dispatcher.InvokeAsync(() => IsOn = _ask.IsOn);

    void OnFillChanged(object? sender, long documentId)
    {
        if (documentId == _documentId) _dispatcher.InvokeAsync(ShowMeaning);
    }

    bool CanAsk() => !IsAsking && _documentId is not null && Question.Trim().Length > 0;

    /// <summary>Looks for the pages that answer the question (Enter in the box, or Ask).</summary>
    [RelayCommand(CanExecute = nameof(CanAsk))]
    async Task Ask()
    {
        if (_documentId is not { } documentId) return;
        var question = Question.Trim();
        Clear();
        var version = _version;
        IsAsking = true;
        Asked = question;
        try
        {
            var findings = await Task.Run(() => _ask.FindAsync(documentId, question));
            if (version != _version) return;
            Show(findings);
        }
        catch (AskUnavailableException ex)
        {
            if (version == _version) Message = ex.Message;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.LogError(ex, "Asking document {DocumentId} failed", documentId);
            if (version == _version) Message = "That didn't work. The log has the details.";
        }
        finally
        {
            if (version == _version) IsAsking = false;
        }
    }

    void Show(AskFindings findings)
    {
        _findings = findings;
        foreach (var passage in findings.Passages) Passages.Add(new AskPassageRow(passage));
        HasPassages = Passages.Count > 0;
        Message = findings.Weak
            ? "Nothing in this book looks like it's about that." + (findings.LookedAt.Count > 0 ? $" These pages came closest: {Pages(findings.LookedAt)}." : "")
            : null;
        var coverage = findings.Coverage;
        CoverageText = coverage.Pages == 0 ? "This book has no pages with text yet."
            : $"{Count(coverage.WithText)} of {Count(coverage.Pages)} pages have text" + (coverage.AwaitingOcr > 0 ? $"; {Count(coverage.AwaitingOcr)} await OCR." : ".");
        ShowMeaning();
    }

    /// <summary>Says how pages were found, and how far the book's vectors have got since.</summary>
    void ShowMeaning()
    {
        if (_findings is not { } findings || _documentId is not { } documentId) return;
        var fill = _ask.FillOf(documentId);
        MeaningText = findings.Meaning switch
        {
            AskMeaning.Off => "Found by words alone. Choose an embedding model in Settings > AI to find passages by meaning too.",
            _ when findings.MeaningProblem is { } problem => $"Found by words alone this time: {problem}",
            AskMeaning.On => "Found by words and by meaning.",
            _ when fill is { Running: true } => $"Found by words, and by meaning where it could: {Count(fill.Done)} of {Count(fill.Total)} pages are read for meaning so far, the rest in the background.",
            _ when fill is { Problem: { } stopped } => $"Found by words, and by meaning where it could. Reading the rest for meaning stopped: {stopped}",
            _ when fill is { Done: var done, Total: var total } && done >= total && total > 0 => "Found by words, and partly by meaning. Every page is read for meaning now: ask again to use them all.",
            _ => "Found by words, and by meaning where it could.",
        };
    }

    /// <summary>Opens the passage's page with its matching word marked.</summary>
    [RelayCommand]
    Task GoTo(AskPassageRow row) => _show(row.Passage.PdfPage, row.Passage.Phrase);

    /// <summary>"page 4", or "pages 112, 113 and 140".</summary>
    public static string Pages(IReadOnlyList<string> pages) => pages.Count switch
    {
        0 => "",
        1 => $"page {pages[0]}",
        _ => $"pages {string.Join(", ", pages.Take(pages.Count - 1))} and {pages[^1]}",
    };

    static string Count(int count) => count.ToString("N0", CultureInfo.CurrentCulture);
}
