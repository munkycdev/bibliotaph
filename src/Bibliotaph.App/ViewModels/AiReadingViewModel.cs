using System.Collections.ObjectModel;
using System.Globalization;
using Bibliotaph.App.Services;
using Bibliotaph.Classification;
using Bibliotaph.Core.Metadata;
using Bibliotaph.Processing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bibliotaph.App.ViewModels;

/// <summary>A book the AI has read, in the AI reading page's list: what it kept, what it dropped and why, or why it failed.</summary>
public sealed class AiReadingDoneItem(string title, string summary, string? problem, IReadOnlyList<AiTestLine> kept, IReadOnlyList<AiTestLine> dropped)
{
    public string Title { get; } = title;

    /// <summary>"Kept 5 values, dropped 2 · 41 s".</summary>
    public string Summary { get; } = summary;

    public string? Problem { get; } = problem;

    public IReadOnlyList<AiTestLine> Kept { get; } = kept;

    public IReadOnlyList<AiTestLine> Dropped { get; } = dropped;
}

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

    public ObservableCollection<AiReadingDoneItem> Recent { get; } = [];

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
            StepText = book.Step switch
            {
                AiReadingStep.Reading => $"Reading the excerpt with {book.Model}",
                AiReadingStep.Writing => $"{book.Model} is writing its answer",
                _ => $"Waiting for {book.Model}: a model that isn't loaded yet takes a little while to start",
            };
            ShowReadProgress = book.Read is not null;
            ReadPercent = (book.Read ?? 0) * 100;
            Fields.Clear();
            foreach (var field in book.Fields)
                Fields.Add(new AiTestLine(MetadataFields.Find(field.Name)?.Label ?? field.Name, field.Values.Count == 0 ? "…" : string.Join(", ", field.Values)));
        }
        else
        {
            Fields.Clear();
            IdleText = !ai.Setup.IsReady ? "AI is off, or not set up yet, so no book is being read."
                : Activity.AiPaused ? "Cataloguing with AI is paused."
                : Activity.IsAiWaiting ? $"Cataloguing with AI is waiting: {Activity.AiWaiting}"
                : "No book is being read right now. Books are read one at a time as they're added or changed.";
        }

        var recent = feed.Recent;
        if (recent.Count > 0 && ReferenceEquals(recent[0], _recentShown)) return;
        _recentShown = recent.Count > 0 ? recent[0] : null;
        Recent.Clear();
        foreach (var done in recent) Recent.Add(Describe(done));
        HasRecent = Recent.Count > 0;
    }

    AiReadingDoneItem Describe(AiReadingBook book)
    {
        var took = book.Took is { } t ? $" · {Seconds(t)}" : "";
        if (book.Result is not { } result)
            return new AiReadingDoneItem(book.Title, $"Couldn't be read{took}", book.Problem, [], []);
        var kept = result.Accepted.Select(claim => new AiTestLine($"{claim.Field.Label}: {Value(claim)}",
            $"Page {ClassifierPrompt.Marker(claim.PdfPage).ToString(CultureInfo.CurrentCulture)}{(claim.FromSampling ? ", from sampled pages" : "")}: “{claim.Quote}”")).ToList();
        var dropped = result.Dropped.Select(d => new AiTestLine($"{MetadataFields.Find(d.Claim.Field)?.Label ?? d.Claim.Field}: {d.Claim.Value}", d.Reason)).ToList();
        return new AiReadingDoneItem(book.Title, $"Kept {Count(kept.Count)}, dropped {dropped.Count.ToString(CultureInfo.CurrentCulture)}{took}", null, kept, dropped);
    }

    string Value(CheckedClaim claim) =>
        claim.Field.Kind == FieldKind.Term && !claim.IsNewTerm && _vocabulary is { } vocabulary ? vocabulary.Label(claim.Field.Vocabulary!, claim.Value)
        : claim.IsNewTerm ? $"{claim.Value} (new)"
        : claim.Value;

    static string Count(int values) => values == 1 ? "1 value" : $"{values.ToString(CultureInfo.CurrentCulture)} values";

    static string Seconds(TimeSpan time) => $"{Math.Max(0, time.TotalSeconds).ToString("0", CultureInfo.CurrentCulture)} s";

    [RelayCommand]
    void OpenAiSettings() => links.Open(SettingsSection.Ai);
}
