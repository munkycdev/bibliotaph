using System.Collections.ObjectModel;
using Bibliotaph.App.Services;
using Bibliotaph.Core.Metadata;
using Bibliotaph.Processing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bibliotaph.App.ViewModels;

/// <summary>
/// AI reading: the book the Classify lane is reading now, its step (waiting for the model, reading the excerpt with a
/// progress bar when the server says how far it has got, writing), the answer's fields as the model writes them, and
/// the last 20 books with what the evidence check kept and dropped. The page looks at the feed four times a second,
/// since an answer streams faster than it is worth redrawing.
/// </summary>
public sealed partial class AiReadingViewModel(AiReadingFeed feed, AiService ai, LibraryActivity activity, SettingsLinks links, TimeProvider time)
    : PageViewModel
{
    UiTimer? _timer;
    volatile bool _dirty;
    Vocabulary? _vocabulary;

    public override Route Route => Route.AiReading;

    public override string Title => "AI reading";

    public override string Section => "Settings";

    public override Route? SectionRoute => Route.Settings;

    public LibraryActivity Activity { get; } = activity;

    [ObservableProperty]
    public partial bool HasCurrent { get; private set; }

    [ObservableProperty]
    public partial string CurrentTitle { get; private set; } = "";

    [ObservableProperty]
    public partial string StepText { get; private set; } = "";

    [ObservableProperty]
    public partial string ElapsedText { get; private set; } = "";

    /// <summary>The server says how far it has read the excerpt (llama.cpp's server does; Ollama doesn't).</summary>
    [ObservableProperty]
    public partial bool ShowReadProgress { get; private set; }

    /// <summary>0 to 100.</summary>
    [ObservableProperty]
    public partial double ReadPercent { get; private set; }

    /// <summary>The answer's fields so far: "Title: The Sunken Lantern".</summary>
    public ObservableCollection<AiTestLine> Fields { get; } = [];

    public ObservableCollection<AiReadingDone> Recent { get; } = [];

    [ObservableProperty]
    public partial bool HasRecent { get; private set; }

    /// <summary>With no book being read: why not.</summary>
    [ObservableProperty]
    public partial string IdleText { get; private set; } = "";

    public override async Task LoadAsync()
    {
        _vocabulary = await ai.VocabularyAsync();
        feed.Changed -= OnChanged;
        feed.Changed += OnChanged;
        _timer ??= new UiTimer(time, TimeSpan.FromMilliseconds(250), repeat: true, Tick);
        _timer.Start();
        _recentShown = null;
        Refresh();
    }

    public override void Unload()
    {
        feed.Changed -= OnChanged;
        _timer?.Stop();
    }

    void OnChanged(object? sender, EventArgs e) => _dirty = true;

    void Tick()
    {
        if (_dirty)
        {
            _dirty = false;
            Refresh();
        }
        else if (feed.Current is { } book) ElapsedText = Seconds(time.GetUtcNow() - book.Started);
    }

    AiReadingBook? _recentShown;

    void Refresh()
    {
        var book = feed.Current;
        HasCurrent = book is not null;
        if (book is not null)
        {
            CurrentTitle = book.Title;
            ElapsedText = Seconds(time.GetUtcNow() - book.Started);
            StepText = AiReadingText.Step(book);
            ShowReadProgress = book.Read is not null;
            ReadPercent = (book.Read ?? 0) * 100;
            Fields.Clear();
            foreach (var field in AiReadingText.Fields(book)) Fields.Add(field);
        }
        else
        {
            Fields.Clear();
            IdleText = AiReadingText.Idle(ai.Setup.IsReady, Activity.AiPaused, Activity.AiWaiting);
        }

        var recent = feed.Recent;
        if (recent.Count > 0 && ReferenceEquals(recent[0], _recentShown)) return;
        _recentShown = recent.Count > 0 ? recent[0] : null;
        Recent.Clear();
        foreach (var done in recent) Recent.Add(AiReadingText.Describe(done, _vocabulary ?? Vocabulary.Empty));
        HasRecent = Recent.Count > 0;
    }

    static string Seconds(TimeSpan time) => AiReadingText.Seconds(time);

    [RelayCommand]
    void OpenAiSettings() => links.Open(SettingsSection.Ai);
}
