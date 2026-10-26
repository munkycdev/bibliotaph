using System.Collections.ObjectModel;
using System.Windows.Threading;
using Bibliotaph.App.Services;
using Bibliotaph.Catalog;
using Bibliotaph.Classification;
using Bibliotaph.Core.Search;
using Bibliotaph.Index;
using Bibliotaph.Processing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.App.ViewModels;

/// <summary>A page an answer from the library was read from: "The Sunken Keep · p. 112 · The Mill", a few of its lines, and how it was found.</summary>
public sealed class LibraryAskPassageRow(LibraryAskPassage passage)
{
    public LibraryAskPassage Passage { get; } = passage;

    /// <summary>"The Sunken Keep · p. 112 · The Mill", or without the bookmark for a page under none.</summary>
    public string Link => LibraryAskText.Link(Passage);

    public string Text => Passage.Text;

    /// <summary>"Found by its words", "by its meaning", or both.</summary>
    public string Found => LibraryAskText.Found(Passage);

    public string AccessibleName => LibraryAskText.Open(Passage);
}

/// <summary>
/// Ask your library, in the top bar's Ask panel: a question answered from pages across the books the Library shows (its
/// filters and the search box's fields), the answer streamed from the model with each citation naming its book and
/// page and opening that book there, and the pages it was read from. When nothing looks like it's about the question,
/// it says so and shows which pages came closest, rather than asking the model.
/// </summary>
public sealed partial class LibraryAskViewModel : ObservableObject, IDisposable
{
    readonly LibraryAskService _ask;
    readonly LibraryAskScope _scope;
    readonly LibraryStore _library;
    readonly ReaderWindows _readers;
    readonly ILogger<LibraryAskViewModel> _log;
    readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    int _version;
    CancellationTokenSource? _answering;

    public LibraryAskViewModel(LibraryAskService ask, LibraryAskScope scope, LibraryStore library, ReaderWindows readers, ILogger<LibraryAskViewModel> log)
    {
        _ask = ask;
        _scope = scope;
        _library = library;
        _readers = readers;
        _log = log;
        _scope.Changed += (_, _) => _dispatcher.InvokeAsync(() => OnPropertyChanged(nameof(ScopeText)));
    }

    /// <summary>A question has been asked: the answer area shows.</summary>
    [ObservableProperty]
    public partial bool IsShown { get; private set; }

    /// <summary>Finding the pages, or waiting for the answer.</summary>
    [ObservableProperty]
    public partial bool IsAsking { get; private set; }

    /// <summary>The model is answering: Stop is offered.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    public partial bool IsAnswering { get; private set; }

    /// <summary>"Looking through your books…", "Reading the pages… 40%", "Writing…".</summary>
    [ObservableProperty]
    public partial string? Step { get; private set; }

    /// <summary>The question the answer is for.</summary>
    [ObservableProperty]
    public partial string? Asked { get; private set; }

    /// <summary>The answer so far, as checked: text, quotations, and citations that open their book at their page.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAnswer))]
    public partial IReadOnlyList<AnswerPart> Answer { get; private set; } = [];

    public bool HasAnswer => Answer.Count > 0;

    /// <summary>The finished answer, or null while there is none.</summary>
    public LibraryAskAnswer? Answered { get; private set; }

    public ObservableCollection<LibraryAskPassageRow> Passages { get; } = [];

    [ObservableProperty]
    public partial bool HasPassages { get; private set; }

    /// <summary>Over the pages: "Read from 4 pages in 3 books" once answered from them, else what they are.</summary>
    [ObservableProperty]
    public partial string PassagesHeading { get; private set; } = AskText.PagesHeading;

    /// <summary>Why there is no answer: nothing looks like it's about the question, the pages don't answer it, or AI is off.</summary>
    [ObservableProperty]
    public partial string? Message { get; private set; }

    /// <summary>How much was searched, and whether by meaning as well as by words.</summary>
    [ObservableProperty]
    public partial string? CoverageText { get; private set; }

    /// <summary>Which books a question goes to: "Answers come from the books the Library shows: D&amp;D 5e, Adventure."</summary>
    public string ScopeText => AskPanelText.Scope(_scope.Labels);

    /// <summary>Puts the answer away: Ask was closed, or a request is being read as a search instead.</summary>
    public void Clear()
    {
        StopAnswering();
        _version++;
        IsShown = false;
        IsAsking = false;
        Show(null);
    }

    void Show(string? asked)
    {
        Answered = null;
        Asked = asked;
        Answer = [];
        Passages.Clear();
        HasPassages = false;
        PassagesHeading = AskText.PagesHeading;
        Message = null;
        CoverageText = null;
        Step = null;
    }

    /// <summary>Stops an answer being written.</summary>
    public void Dispose() => StopAnswering();

    void StopAnswering()
    {
        _answering?.Cancel();
        _answering?.Dispose();
        _answering = null;
        IsAnswering = false;
    }

    /// <summary>
    /// Finds the pages across the books the Library shows that answer <paramref name="question"/>, then has the model
    /// answer from them. A question still being answered is dropped for the new one.
    /// </summary>
    public async Task AskAsync(string question)
    {
        question = question.Trim();
        Clear();
        if (question.Length == 0) return;
        var version = _version;
        IsShown = true;
        IsAsking = true;
        Show(question);
        OnPropertyChanged(nameof(ScopeText));
        try
        {
            Step = LibraryAskText.Looking;
            var filters = await FiltersAsync();
            var findings = await Task.Run(() => _ask.FindAsync(question, filters));
            if (version != _version) return;
            ShowFindings(findings);
            if (!findings.Weak && findings.Evidence.Count > 0 && _ask.CanAnswer) await AnswerAsync(findings, version);
        }
        catch (AskUnavailableException ex)
        {
            if (version == _version) Message = ex.Message;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.LogError(ex, "Asking the library failed");
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

    /// <summary>The Library's filters, or the whole library as the Library shows it before it has been opened.</summary>
    async Task<IReadOnlyList<AskFilter>> FiltersAsync()
    {
        if (_scope.Filter is { } filter) return [new BookFilter(filter, _scope.Plan)];
        var visible = await Task.Run(() => _library.GetVisibleEntryIdsAsync());
        return [new BookFilter(new LibraryFilter(visible), new SearchPlan())];
    }

    async Task AnswerAsync(LibraryAskFindings findings, int version)
    {
        var pages = LibraryAskService.PromptPages(findings);
        var stop = _answering = new CancellationTokenSource();
        IsAnswering = true;
        Step = AskText.Waiting;
        try
        {
            var answer = await Task.Run(() => _ask.AnswerAsync(findings, new Streamed(this, version, pages, findings.Evidence), stop.Token), stop.Token);
            if (version != _version) return;
            Answered = answer;
            PassagesHeading = LibraryAskText.ReadFrom(findings);
            if (answer.Checked.NotAnswered)
            {
                Answer = [];
                Message = AskText.NotAnswered;
            }
            else Answer = answer.Checked.Parts;
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
    }

    /// <summary>The answer's progress, taken to the UI thread at most once per dispatch, so a fast model doesn't queue a redraw for every word.</summary>
    sealed class Streamed(LibraryAskViewModel panel, int version, IReadOnlyList<AskPromptPage> pages, IReadOnlyList<LibraryAskEvidence> evidence)
        : IProgress<ModelProgress>
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
                if (Volatile.Read(ref _latest) is { } latest) panel.ShowProgress(version, latest, pages, evidence);
            }, DispatcherPriority.Background);
        }
    }

    void ShowProgress(int version, ModelProgress progress, IReadOnlyList<AskPromptPage> pages, IReadOnlyList<LibraryAskEvidence> evidence)
    {
        if (version != _version || !IsAnswering) return;
        Step = AskText.Step(progress);
        if (progress.Step == ModelStep.Writing) Answer = LibraryAskService.Check(progress.Answer, pages, evidence).Parts;
    }

    [RelayCommand(CanExecute = nameof(IsAnswering))]
    void Stop() => _answering?.Cancel();

    void ShowFindings(LibraryAskFindings findings)
    {
        foreach (var passage in findings.Passages) Passages.Add(new LibraryAskPassageRow(passage));
        HasPassages = Passages.Count > 0;
        Message = LibraryAskText.Weak(findings);
        CoverageText = LibraryAskText.Coverage(findings);
    }

    /// <summary>Opens the passage's book at its page, with its matching word marked.</summary>
    [RelayCommand]
    void GoTo(LibraryAskPassageRow row) => Open(row.Passage.DocumentId, row.Passage.Book, row.Passage.PdfPage, row.Passage.Phrase);

    /// <summary>
    /// Opens the book and page a citation names, marking the quotation it follows when that was on the page, else the
    /// word the page was found by.
    /// </summary>
    [RelayCommand]
    void OpenCitation(AnswerPart part)
    {
        if (part is not { PdfPage: { } page, DocumentId: { } documentId }) return;
        var passage = Passages.FirstOrDefault(p => p.Passage.DocumentId == documentId && p.Passage.PdfPage == page)?.Passage;
        var book = passage?.Book ?? Answered?.Cited.FirstOrDefault(c => c.DocumentId == documentId)?.Book ?? "";
        Open(documentId, book, page, part.Phrase ?? passage?.Phrase);
    }

    void Open(long documentId, string book, int pdfPage, string? phrase)
    {
        var words = phrase?.Replace("\"", "", StringComparison.Ordinal).Trim();
        var query = words is { Length: > 0 } ? SearchQuery.Parse($"\"{words}\"") : null;
        _readers.OpenInMainWindow(new ViewerRequest(documentId, book, pdfPage, query));
    }
}
