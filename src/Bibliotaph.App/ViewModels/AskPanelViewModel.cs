using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Threading;
using Bibliotaph.Classification;
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

/// <summary>An earlier turn of the book's conversation, as the panel lists it: the question, what it looked for, and the answer.</summary>
public sealed class AskTurnRow(AskTurnView turn)
{
    public AskTurnView Turn { get; } = turn;

    public string Question => Turn.Info.Question;

    /// <summary>"Looked for: where is the red dragon's lair?", for a follow-up the model rewrote; else null.</summary>
    public string? LookedFor => Turn.Info.Rewritten is { } rewritten ? $"Looked for: {rewritten}" : null;

    public IReadOnlyList<AnswerPart> Answer => Turn.Parts;
}

/// <summary>
/// The reader's Ask panel (Ask this book): a question about the open book, the answer streamed from the model with each
/// [p. N] a link into the book, and the pages it was read from, each opening its page with the matching word marked
/// by Find, with how much of the book has text and whether pages were found by meaning as well as by words. Each
/// question and answer is kept as the book's conversation: a follow-up is rewritten by the model to stand on its own
/// before its pages are found, and is answered with the last few turns. Shown only while AI is on.
/// </summary>
public sealed partial class AskPanelViewModel : ObservableObject, IDisposable
{
    const string PagesHeading = "Pages that look like they answer it";

    readonly AskService _ask;
    readonly Dispatcher _dispatcher;
    readonly ILogger _log;
    readonly Func<int, string?, Task> _show;
    long? _documentId;
    int _version;
    CancellationTokenSource? _answering;
    AskTurnView? _current;

    /// <param name="ask">Finds the pages and answers from them.</param>
    /// <param name="dispatcher">The reader's, for changes raised on other threads.</param>
    /// <param name="log">Failures; never the question, the answer or the book's text.</param>
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

    /// <summary>Finding the pages, or waiting for the answer to them.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AskCommand), nameof(NewConversationCommand))]
    public partial bool IsAsking { get; private set; }

    /// <summary>The model is answering: Stop is offered.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    public partial bool IsAnswering { get; private set; }

    /// <summary>"Looking through the book…", "Waiting for the model…", "Reading the pages… 40%", "Writing…".</summary>
    [ObservableProperty]
    public partial string? Step { get; private set; }

    /// <summary>The question the results are for, as asked.</summary>
    [ObservableProperty]
    public partial string? Asked { get; private set; }

    /// <summary>"Looked for: …": the follow-up as the model rewrote it to stand on its own, when it did.</summary>
    [ObservableProperty]
    public partial string? LookedFor { get; private set; }

    /// <summary>The book's conversation before the question shown, oldest first.</summary>
    public ObservableCollection<AskTurnRow> History { get; } = [];

    [ObservableProperty]
    public partial bool HasHistory { get; private set; }

    /// <summary>There is a conversation to start again: earlier turns, or the answer shown.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NewConversationCommand))]
    public partial bool CanStartOver { get; private set; }

    /// <summary>The answer so far, as checked: text, quotations, links to the pages it cites, and notes on what was taken out.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAnswer))]
    public partial IReadOnlyList<AnswerPart> Answer { get; private set; } = [];

    public bool HasAnswer => Answer.Count > 0;

    /// <summary>The finished answer, as checked, or null while there is none.</summary>
    public AskAnswer? Answered { get; private set; }

    public ObservableCollection<AskPassageRow> Passages { get; } = [];

    [ObservableProperty]
    public partial bool HasPassages { get; private set; }

    /// <summary>Over the pages: "Read from pages 112, 113 and 140" once answered from them, else what they are.</summary>
    [ObservableProperty]
    public partial string PassagesHeading { get; private set; } = PagesHeading;

    /// <summary>Why there is no answer: nothing looks like it's about the question, the pages don't answer it, or the book can't be asked.</summary>
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
        _current = null;
        History.Clear();
        ShowHistory();
        AskCommand.NotifyCanExecuteChanged();
        _ = LoadHistoryAsync(documentId);
    }

    /// <summary>Lists the book's conversation so far, kept in catalog.db.</summary>
    async Task LoadHistoryAsync(long documentId)
    {
        try
        {
            var turns = await Task.Run(() => _ask.HistoryAsync(documentId));
            if (_documentId != documentId || History.Count > 0 || _current is not null) return;
            foreach (var turn in turns) History.Add(new AskTurnRow(turn));
            ShowHistory();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.LogError(ex, "Reading the Ask conversation of document {DocumentId} failed", documentId);
        }
    }

    void ShowHistory()
    {
        HasHistory = History.Count > 0;
        CanStartOver = HasHistory || _current is not null;
    }

    /// <summary>The reader closed the book: an answer being written stops.</summary>
    public void Detach()
    {
        _ask.Changed -= OnAiChanged;
        _ask.FillChanged -= OnFillChanged;
        _documentId = null;
        StopAnswering();
        _version++;
        IsAsking = false;
        AskCommand.NotifyCanExecuteChanged();
    }

    void Clear()
    {
        StopAnswering();
        _version++;
        _findings = null;
        Answered = null;
        Asked = null;
        LookedFor = null;
        Answer = [];
        Passages.Clear();
        HasPassages = false;
        PassagesHeading = PagesHeading;
        Message = null;
        CoverageText = null;
        MeaningText = null;
        Step = null;
        IsAsking = false;
    }

    void StopAnswering()
    {
        _answering?.Cancel();
        _answering?.Dispose();
        _answering = null;
        IsAnswering = false;
    }

    /// <summary>Stops an answer being written. The panel can still ask again: the reader comes back to it with Back.</summary>
    public void Dispose() => StopAnswering();

    void OnAiChanged(object? sender, EventArgs e) => _dispatcher.InvokeAsync(() => IsOn = _ask.IsOn);

    void OnFillChanged(object? sender, long documentId)
    {
        if (documentId == _documentId) _dispatcher.InvokeAsync(ShowMeaning);
    }

    bool CanAsk() => !IsAsking && _documentId is not null && Question.Trim().Length > 0;

    /// <summary>Finds the pages that answer the question, then has the model answer from them (Enter in the box, or Ask).</summary>
    [RelayCommand(CanExecute = nameof(CanAsk))]
    async Task Ask()
    {
        if (_documentId is not { } documentId) return;
        var question = Question.Trim();
        // The answer shown becomes the latest of the conversation's earlier turns.
        if (_current is { } previous) History.Add(new AskTurnRow(previous));
        _current = null;
        ShowHistory();
        Clear();
        var version = _version;
        IsAsking = true;
        Asked = question;
        var earlier = History.Select(h => h.Turn.Earlier).ToList();
        try
        {
            var looked = question;
            var rewritten = false;
            if (earlier.Count > 0 && _ask.CanAnswer)
            {
                Step = "Working out the follow-up…";
                (looked, rewritten) = await Task.Run(() => _ask.RewriteAsync(documentId, question, earlier));
                if (version != _version) return;
                if (rewritten) LookedFor = $"Looked for: {looked}";
            }
            Step = "Looking through the book…";
            var findings = await Task.Run(() => _ask.FindAsync(documentId, looked));
            if (version != _version) return;
            Show(findings);
            if (!findings.Weak && findings.Evidence.Count > 0 && _ask.CanAnswer
                && await AnswerAsync(documentId, findings, earlier, version) is { } answer)
            {
                var saved = await Task.Run(() => _ask.SaveTurnAsync(documentId, question, rewritten ? looked : null, answer));
                if (version != _version) return;
                _current = saved;
                ShowHistory();
            }
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
            if (version == _version)
            {
                IsAsking = false;
                Step = null;
            }
        }
    }

    /// <summary>Streams the model's answer into the panel, checked as it comes, then shows it checked in full. Null when there is none.</summary>
    async Task<AskAnswer?> AnswerAsync(long documentId, AskFindings findings, IReadOnlyList<AskEarlierTurn> earlier, int version)
    {
        var pages = AskService.PromptPages(findings);
        var stop = _answering = new CancellationTokenSource();
        IsAnswering = true;
        Step = "Waiting for the model…";
        try
        {
            var answer = await Task.Run(() => _ask.AnswerAsync(documentId, findings, new Streamed(this, version, pages), earlier, stop.Token), stop.Token);
            if (version != _version) return null;
            Answered = answer;
            PassagesHeading = $"Read from {Pages([.. pages.Select(p => p.Marker)])}";
            if (answer.Checked.NotAnswered)
            {
                Answer = [];
                Message = "The pages it read don't answer that. They're below, in case they help.";
            }
            else Answer = answer.Checked.Parts;
            return answer;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            if (version == _version) Message = "Stopped. What was written so far is above.";
        }
        catch (ModelEmptyAnswerException)
        {
            if (version == _version) Message = "The model sent back nothing. Try asking another way.";
        }
        catch (ModelEndpointException ex)
        {
            if (version == _version) Message = $"The model couldn't answer: {ex.Message}";
        }
        finally
        {
            if (version == _version) IsAnswering = false;
        }
        return null;
    }

    bool CanNewConversation() => CanStartOver && !IsAsking;

    /// <summary>New conversation: forgets the book's questions and answers so far, so the next question starts afresh.</summary>
    [RelayCommand(CanExecute = nameof(CanNewConversation))]
    async Task NewConversation()
    {
        if (_documentId is not { } documentId) return;
        try
        {
            await Task.Run(() => _ask.ClearHistoryAsync(documentId));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.LogError(ex, "Starting the Ask conversation of document {DocumentId} again failed", documentId);
            Message = "That didn't work. The log has the details.";
            return;
        }
        if (_documentId != documentId) return;
        History.Clear();
        _current = null;
        Clear();
        Question = "";
        ShowHistory();
    }

    /// <summary>
    /// The answer's progress, taken to the reader's thread at most once per dispatch, so a fast model doesn't queue
    /// a redraw for every word.
    /// </summary>
    sealed class Streamed(AskPanelViewModel panel, int version, IReadOnlyList<AskPromptPage> pages) : IProgress<ModelProgress>
    {
        ModelProgress? _latest;
        int _pending;

        public void Report(ModelProgress value)
        {
            Interlocked.Exchange(ref _latest, value);
            if (Interlocked.Exchange(ref _pending, 1) == 1) return;
            panel._dispatcher.InvokeAsync(() =>
            {
                Interlocked.Exchange(ref _pending, 0);
                if (Volatile.Read(ref _latest) is { } latest) panel.ShowProgress(version, latest, pages);
            }, DispatcherPriority.Background);
        }
    }

    void ShowProgress(int version, ModelProgress progress, IReadOnlyList<AskPromptPage> pages)
    {
        if (version != _version || !IsAnswering) return;
        Step = progress.Step switch
        {
            ModelStep.Reading when progress.Read is { } read => $"Reading the pages… {read.ToString("P0", CultureInfo.CurrentCulture)}",
            ModelStep.Reading => "Reading the pages…",
            ModelStep.Writing => "Writing…",
            _ => "Waiting for the model…",
        };
        if (progress.Step == ModelStep.Writing) Answer = AskAnswerCheck.Check(progress.Answer, pages).Parts;
    }

    [RelayCommand(CanExecute = nameof(IsAnswering))]
    void Stop() => _answering?.Cancel();

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

    /// <summary>
    /// Opens the page a citation in the answer names, marking the quotation it follows when that was on the page, else
    /// the word the page was found by.
    /// </summary>
    [RelayCommand]
    Task OpenCitation(AnswerPart part)
    {
        if (part.PdfPage is not { } page) return Task.CompletedTask;
        var phrase = part.Phrase ?? Passages.FirstOrDefault(p => p.Passage.PdfPage == page)?.Passage.Phrase;
        return _show(page, phrase);
    }

    /// <summary>"page 4", or "pages 112, 113 and 140".</summary>
    public static string Pages(IReadOnlyList<string> pages) => pages.Count switch
    {
        0 => "",
        1 => $"page {pages[0]}",
        _ => $"pages {string.Join(", ", pages.Take(pages.Count - 1))} and {pages[^1]}",
    };

    static string Count(int count) => count.ToString("N0", CultureInfo.CurrentCulture);
}
