using System.Collections.ObjectModel;
using System.Windows.Threading;
using Bibliotaph.Classification;
using Bibliotaph.Processing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Bibliotaph.App.Services;

namespace Bibliotaph.App.ViewModels;

/// <summary>A page shown under a question in the Ask panel: "p. 112 · The Mill", a few of its lines, and how it was found.</summary>
public sealed class AskPassageRow(AskPassage passage)
{
    public AskPassage Passage { get; } = passage;

    /// <summary>"p. 112 · The Mill", or "p. 112" for a page under no bookmark.</summary>
    public string Link => AskText.Link(Passage);

    public string Text => Passage.Text;

    /// <summary>"Found by its words", "by its meaning", or both.</summary>
    public string Found => AskText.Found(Passage);

    public string AccessibleName => $"Go to page {Passage.Page}";
}

/// <summary>An earlier turn of the book's conversation, as the panel lists it: the question, what it looked for, and the answer.</summary>
public sealed class AskTurnRow(AskTurnView turn)
{
    public AskTurnView Turn { get; } = turn;

    public string Question => Turn.Info.Question;

    /// <summary>"Looked for: where is the red dragon's lair?", for a follow-up the model rewrote; else null.</summary>
    public string? LookedFor => Turn.Info.Rewritten is { } rewritten ? AskText.LookedFor(rewritten) : null;

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
    public partial string PassagesHeading { get; private set; } = AskText.PagesHeading;

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
        LoadHistoryAsync(documentId).FireAndForget();
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
        PassagesHeading = AskText.PagesHeading;
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
                Step = AskText.Rewriting;
                (looked, rewritten) = await Task.Run(() => _ask.RewriteAsync(documentId, question, earlier));
                if (version != _version) return;
                if (rewritten) LookedFor = AskText.LookedFor(looked);
            }
            Step = AskText.Looking;
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
            if (version == _version) Message = AskText.Failed;
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
        Step = AskText.Waiting;
        try
        {
            var answer = await Task.Run(() => _ask.AnswerAsync(documentId, findings, new Streamed(this, version, pages), earlier, stop.Token), stop.Token);
            if (version != _version) return null;
            Answered = answer;
            PassagesHeading = AskText.ReadFrom(pages);
            if (answer.Checked.NotAnswered)
            {
                Answer = [];
                Message = AskText.NotAnswered;
            }
            else Answer = answer.Checked.Parts;
            return answer;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            if (version == _version) Message = AskText.Stopped;
        }
        catch (ModelEmptyAnswerException)
        {
            if (version == _version) Message = AskText.Empty;
        }
        catch (ModelEndpointException ex)
        {
            if (version == _version) Message = AskText.CouldNotAnswer(ex.Message);
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
            Message = AskText.Failed;
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
        Step = AskText.Step(progress);
        if (progress.Step == ModelStep.Writing) Answer = AskAnswerCheck.Check(progress.Answer, pages).Parts;
    }

    [RelayCommand(CanExecute = nameof(IsAnswering))]
    void Stop() => _answering?.Cancel();

    void Show(AskFindings findings)
    {
        _findings = findings;
        foreach (var passage in findings.Passages) Passages.Add(new AskPassageRow(passage));
        HasPassages = Passages.Count > 0;
        Message = AskText.Weak(findings);
        CoverageText = AskText.Coverage(findings.Coverage);
        ShowMeaning();
    }

    /// <summary>Says how pages were found, and how far the book's vectors have got since.</summary>
    void ShowMeaning()
    {
        if (_findings is not { } findings || _documentId is not { } documentId) return;
        MeaningText = AskText.Meaning(findings, _ask.FillOf(documentId));
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
}
