using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Bibliotaph.App.Services;
using Bibliotaph.Classification;
using Bibliotaph.Core.Metadata;
using Bibliotaph.Processing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bibliotaph.App.ViewModels;

/// <summary>
/// The Pilot section of Settings > AI, shown only with --pilot (slice 2d): pick the books, run the models overnight,
/// review the answers, and make the report.
/// </summary>
public sealed partial class PilotPanelViewModel : ObservableObject
{
    readonly PilotService _pilot;
    readonly INavigationService _navigation;

    public PilotPanelViewModel(PilotService pilot, PilotSession session, INavigationService navigation)
    {
        _pilot = pilot;
        _navigation = navigation;
        Session = session;
    }

    public PilotSession Session { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(ReviewCommand), nameof(OpenFolderCommand), nameof(ReportCommand))]
    public partial int Books { get; private set; }

    [ObservableProperty]
    public partial string BooksText { get; private set; } = "";

    [ObservableProperty]
    public partial string? ReadText { get; private set; }

    [ObservableProperty]
    public partial string ReviewLabel { get; private set; } = "Review answers";

    [ObservableProperty]
    public partial string? Note { get; private set; }

    public async Task LoadAsync()
    {
        // Settings loads its AI section again when it is chosen while showing, without an Unload in between.
        Unload();
        Session.PropertyChanged += OnSessionChanged;
        Session.Stopped += OnStopped;
        await RefreshAsync();
    }

    public void Unload()
    {
        Session.PropertyChanged -= OnSessionChanged;
        Session.Stopped -= OnStopped;
    }

    void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PilotSession.IsRunning)) return;
        StartCommand.NotifyCanExecuteChanged();
        PauseCommand.NotifyCanExecuteChanged();
        PickBooksCommand.NotifyCanExecuteChanged();
    }

    void OnStopped(object? sender, EventArgs e) => _ = RefreshAsync();

    async Task RefreshAsync()
    {
        var status = await _pilot.GetStatusAsync();
        Books = status.Books;
        BooksText = status.Books == 0
            ? "No books picked yet. Pick lets Bibliotaph choose them, spread across your folders and game systems."
            : $"{status.Books:N0} books on the list. You've answered {status.Answered:N0}.";
        ReadText = status.ReadByModel.Count == 0 ? null
            : "Read so far: " + string.Join(", ", status.ReadByModel.Select(m => $"{m.Key} {m.Value.ToString("N0", CultureInfo.CurrentCulture)}")) + ".";
        ReviewLabel = $"Review answers ({status.Answered:N0} of {status.Books:N0})";
    }

    bool CanPick() => !Session.IsRunning;

    [RelayCommand(CanExecute = nameof(CanPick))]
    async Task PickBooksAsync()
    {
        var books = await _pilot.PickBooksAsync();
        Note = books.Count == 0
            ? "No book has text on at least three pages yet. Wait for indexing, or add a folder."
            : $"Picked {books.Count:N0} books. The list is books.txt in the pilot folder; edit it before you start if you like.";
        await RefreshAsync();
    }

    bool HasBooks() => Books > 0;

    [RelayCommand(CanExecute = nameof(HasBooks))]
    void OpenFolder() => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_pilot.Store.Folder}\"") { UseShellExecute = false })?.Dispose();

    bool CanStart() => Books > 0 && !Session.IsRunning;

    [RelayCommand(CanExecute = nameof(CanStart))]
    Task StartAsync()
    {
        Note = null;
        return Session.StartAsync();
    }

    bool CanPause() => Session.IsRunning;

    [RelayCommand(CanExecute = nameof(CanPause))]
    void Pause() => Session.Pause();

    [RelayCommand(CanExecute = nameof(HasBooks))]
    void Review() => _navigation.NavigateTo(Route.PilotReview);

    [RelayCommand(CanExecute = nameof(HasBooks))]
    async Task ReportAsync()
    {
        var path = await _pilot.WriteReportAsync();
        Note = $"The report is {path}. It stays on this computer.";
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
    }
}

/// <summary>A value offered for a field, from some model or the catalog; "Use" puts it in the answer.</summary>
public sealed partial class PilotOptionViewModel(PilotOption option, Action<string> use)
{
    public string Text { get; } = option.Text;

    /// <summary>The page and quote that back it, when a model gave it.</summary>
    public string? Detail { get; } = option.PdfPage is { } page
        ? $"Page {ClassifierPrompt.Marker(page).ToString(CultureInfo.CurrentCulture)}: “{option.Quote}”"
        : null;

    [RelayCommand]
    void Use() => use(Text);
}

/// <summary>One field on the review page: the values offered, and the user's answer or "Not in this book".</summary>
public sealed partial class PilotFieldViewModel : ObservableObject
{
    public PilotFieldViewModel(PilotFieldReview review)
    {
        Field = review.Field;
        Options = [.. review.Options.Select(o => new PilotOptionViewModel(o, Use))];
        Answer = review.Answer;
        NotInBook = review.NotInBook;
    }

    public MetadataField Field { get; }

    public string Label => Field.Label;

    public string? Hint => Field.Multiple ? "Separate them with commas." : Field.Kind == FieldKind.Levels ? "Like 3, 1-5 or n/a." : null;

    public IReadOnlyList<PilotOptionViewModel> Options { get; }

    public bool HasOptions => Options.Count > 0;

    [ObservableProperty]
    public partial string Answer { get; set; }

    [ObservableProperty]
    public partial bool NotInBook { get; set; }

    partial void OnNotInBookChanged(bool value)
    {
        if (value) Answer = "";
    }

    partial void OnAnswerChanged(string value)
    {
        if (value.Length > 0) NotInBook = false;
    }

    /// <summary>A single field takes the value; a list field adds it, unless it is there already.</summary>
    void Use(string text)
    {
        if (!Field.Multiple || Answer.Trim().Length == 0)
        {
            Answer = text;
            return;
        }
        var values = MetadataValues.Split(Field, Answer);
        if (!values.Any(v => MetadataText.Normalize(v) == MetadataText.Normalize(text))) Answer = string.Join(", ", values.Append(text));
    }

    public PilotAnswer? ToAnswer() => NotInBook ? new PilotAnswer("", true) : Answer.Trim().Length > 0 ? new PilotAnswer(Answer, false) : null;
}

/// <summary>
/// The Pilot review page (choice P5): one book at a time, the values the models kept and the catalog's, merged with no
/// model's name on them, and the user's answer for each field. Answers are the pilot's answer key and, by default,
/// the user's own values in the catalog too.
/// </summary>
public sealed partial class PilotReviewViewModel(PilotService pilot, PilotSession session, ReaderWindows readers) : PageViewModel
{
    IReadOnlyList<PilotBook> _books = [];
    HashSet<long> _answered = [];
    int _index;

    public override Route Route => Route.PilotReview;
    public override string Title => "Pilot review";
    public override string Section => "Settings";
    public override Route? SectionRoute => Route.Settings;

    public PilotSession Session { get; } = session;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PreviousCommand), nameof(SaveCommand), nameof(SkipCommand), nameof(OpenBookCommand))]
    public partial PilotBookReview? Book { get; private set; }

    public ObservableCollection<PilotFieldViewModel> Fields { get; } = [];

    [ObservableProperty]
    public partial string PositionText { get; private set; } = "";

    [ObservableProperty]
    public partial string? Problem { get; private set; }

    [ObservableProperty]
    public partial string? Note { get; private set; }

    public override async Task LoadAsync()
    {
        _books = await pilot.LoadBooksAsync();
        _answered = [.. (await pilot.Store.GetAnswersAsync()).Keys];
        await ShowAsync(Math.Max(0, Next(0)));
    }

    async Task ShowAsync(int index)
    {
        Problem = null;
        Fields.Clear();
        if (_books.Count == 0)
        {
            Book = null;
            PositionText = "No books on the pilot list yet. Pick them in Settings > AI.";
            return;
        }
        _index = Math.Clamp(index, 0, _books.Count - 1);
        Book = await pilot.GetReviewAsync(_books[_index]);
        foreach (var field in Book.Fields) Fields.Add(new PilotFieldViewModel(field));
        PositionText = $"Book {_index + 1:N0} of {_books.Count:N0}. You've answered {_answered.Count(id => _books.Any(b => b.DocumentId == id)):N0}.";
    }

    bool HasPrevious() => Book is not null && _index > 0;

    [RelayCommand(CanExecute = nameof(HasPrevious))]
    Task PreviousAsync() => ShowAsync(_index - 1);

    bool HasBook() => Book is not null;

    /// <summary>Saves the answers and goes to the next book not answered yet, or the next one when all are.</summary>
    [RelayCommand(CanExecute = nameof(HasBook))]
    async Task SaveAsync()
    {
        var answers = Fields.Select(f => (f.Field, Answer: f.ToAnswer())).Where(a => a.Answer is not null).ToDictionary(a => a.Field, a => a.Answer!);
        if (await pilot.SaveAnswersAsync(Book!.Book.DocumentId, answers, Session.AlsoCatalog) is { } problem)
        {
            Problem = $"{problem.Field.Label}: {problem.Message}";
            return;
        }
        _answered.Add(Book.Book.DocumentId);
        var next = Next(_index + 1);
        Note = next < 0 && _index == _books.Count - 1 ? "That was the last book. Make the report in Settings > AI." : null;
        await ShowAsync(next >= 0 ? next : _index + 1);
    }

    /// <summary>The first book from <paramref name="from"/> on that isn't answered, or -1.</summary>
    int Next(int from)
    {
        for (var i = from; i < _books.Count; i++)
            if (!_answered.Contains(_books[i].DocumentId)) return i;
        return -1;
    }

    [RelayCommand(CanExecute = nameof(HasBook))]
    Task SkipAsync() => ShowAsync(_index + 1 < _books.Count ? _index + 1 : 0);

    [RelayCommand(CanExecute = nameof(HasBook))]
    Task OpenBookAsync() => readers.OpenInNewWindowAsync(new ViewerRequest(Book!.Book.DocumentId, Book.Title));
}
