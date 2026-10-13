using System.Collections.ObjectModel;
using System.ComponentModel;
using Bibliotaph.App.Services;
using Bibliotaph.Classification;
using Bibliotaph.Core.Search;
using Bibliotaph.Processing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.App.ViewModels;

/// <summary>Where Ask is: waiting for a request, without AI, reading one, showing its reading, or stuck.</summary>
public enum PlainSearchState
{
    Waiting,
    NeedsAi,
    Reading,
    Interpreted,
    Problem,
}

/// <summary>
/// One chip of Ask's reading: "system:" and an editable value, or "words:" for words to find. Its clause is what it
/// adds to the search; a chip whose value is emptied adds nothing.
/// </summary>
public sealed partial class PlainSearchPartViewModel(InterpretedPart part) : ObservableObject
{
    public SearchFieldInfo? Field { get; } = part.Field;

    public bool Exclude { get; } = part.Exclude;

    /// <summary>"system:", "not theme:", "words:".</summary>
    public string Name => (Exclude ? "not " : "") + (Field is null ? "words:" : Field.Prefix);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Clause), nameof(RemoveName))]
    public partial string Value { get; set; } = part.Value;

    public string? Clause => QueryClause.Format(Field, Value, Exclude);

    /// <summary>The value box's name for screen readers: "system value".</summary>
    public string ValueName => (Field is null ? "words" : Field.Name) + " value";

    public string RemoveName => $"Remove {Name} {Value}";
}

/// <summary>
/// Ask, beside the search box: a request in plain English, read by AI as the search box's own fields and words and
/// shown as chips with notes before anything runs. The chips can be changed or removed; Search puts them in the box
/// as ordinary query text and runs it, so the syntax stays in view and can be changed too. Without AI, Ask says it
/// needs it and links to Settings > AI, and the search box works as it always has.
/// </summary>
public sealed partial class PlainSearchViewModel(PlainSearchService plainSearch, SettingsLinks settings, ILogger<PlainSearchViewModel> log) : ObservableObject
{
    /// <summary>Stops the request being read, while one is.</summary>
    Action? _cancel;

    /// <summary>Ask mode: the box takes a request, Enter reads it, and typing doesn't search.</summary>
    [ObservableProperty]
    public partial bool IsOn { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsAi), nameof(IsReading), nameof(IsInterpreted), nameof(HasParts), nameof(Message))]
    [NotifyCanExecuteChangedFor(nameof(SearchCommand))]
    public partial PlainSearchState State { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Message))]
    public partial string? Problem { get; private set; }

    public bool NeedsAi => State == PlainSearchState.NeedsAi;

    public bool IsReading => State == PlainSearchState.Reading;

    /// <summary>A reading is shown: its notes, and its chips while it has any.</summary>
    public bool IsInterpreted => State == PlainSearchState.Interpreted;

    public bool HasParts => State == PlainSearchState.Interpreted && Parts.Count > 0;

    /// <summary>The chips: what will be searched for.</summary>
    public ObservableCollection<PlainSearchPartViewModel> Parts { get; } = [];

    /// <summary>What was changed or left out, and why.</summary>
    public ObservableCollection<string> Notes { get; } = [];

    /// <summary>The chips as the search box's query text, which Search runs.</summary>
    public string QueryText => QueryClause.Join(Parts.Select(p => p.Clause));

    /// <summary>The line at the top of the Ask panel: what to do now, or what went wrong.</summary>
    public string Message => State switch
    {
        PlainSearchState.NeedsAi => PlainSearchText.NeedsAi,
        PlainSearchState.Reading => "Reading your question…",
        PlainSearchState.Problem => Problem ?? "",
        PlainSearchState.Interpreted when Parts.Count == 0 => "Nothing in that could be searched for. Try saying it another way.",
        PlainSearchState.Interpreted => "Here's how that reads as a search. Change or remove anything, then press Search.",
        _ => "Describe what you're looking for, such as “a one-shot heist for 4 players in a city”, and press Enter.",
    };

    /// <summary>Raised by Search with the query text to put in the box and run.</summary>
    public event EventHandler<string>? SearchRequested;

    partial void OnIsOnChanged(bool value)
    {
        Cancel();
        Clear();
        State = value && !plainSearch.IsReady ? PlainSearchState.NeedsAi : PlainSearchState.Waiting;
    }

    /// <summary>The Ask button: Ask mode on or off.</summary>
    [RelayCommand]
    void Toggle() => IsOn = !IsOn;

    /// <summary>The panel's ×, or Esc in the box.</summary>
    [RelayCommand]
    void Close() => IsOn = false;

    /// <summary>"Set AI up": Settings > AI, leaving Ask.</summary>
    [RelayCommand]
    void OpenAiSettings()
    {
        IsOn = false;
        settings.Open(SettingsSection.Ai);
    }

    /// <summary>Enter in Ask mode: reads the request and shows its chips. A request still being read is dropped for the new one.</summary>
    public async Task InterpretAsync(string request)
    {
        Cancel();
        Clear();
        if (!plainSearch.IsReady)
        {
            State = PlainSearchState.NeedsAi;
            return;
        }
        using var reading = new CancellationTokenSource();
        var cancel = _cancel = reading.Cancel;
        State = PlainSearchState.Reading;
        try
        {
            var result = await plainSearch.InterpretAsync(request, reading.Token);
            if (reading.IsCancellationRequested) return;
            if (result.Interpretation is not { } interpretation)
            {
                Problem = result.Problem;
                State = plainSearch.IsReady ? PlainSearchState.Problem : PlainSearchState.NeedsAi;
                return;
            }
            foreach (var part in interpretation.Parts) Add(new PlainSearchPartViewModel(part));
            foreach (var note in interpretation.Notes) Notes.Add(note);
            State = PlainSearchState.Interpreted;
            OnPropertyChanged(nameof(QueryText));
        }
        catch (OperationCanceledException) when (reading.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Ask failed");
            Problem = "Something went wrong reading your question. Try again.";
            State = PlainSearchState.Problem;
        }
        finally
        {
            if (_cancel == cancel) _cancel = null;
        }
    }

    /// <summary>Stops reading a request.</summary>
    [RelayCommand]
    void Cancel()
    {
        if (_cancel is not { } cancel) return;
        _cancel = null;
        cancel();
        if (State == PlainSearchState.Reading) State = PlainSearchState.Waiting;
    }

    [RelayCommand]
    void Remove(PlainSearchPartViewModel part)
    {
        part.PropertyChanged -= OnPartChanged;
        Parts.Remove(part);
        PartsChanged();
    }

    /// <summary>Puts the chips in the search box as query text and runs it, leaving Ask.</summary>
    [RelayCommand(CanExecute = nameof(CanSearch))]
    void Search()
    {
        var text = QueryText;
        IsOn = false;
        SearchRequested?.Invoke(this, text);
    }

    bool CanSearch() => State == PlainSearchState.Interpreted && QueryText.Length > 0;

    void Add(PlainSearchPartViewModel part)
    {
        part.PropertyChanged += OnPartChanged;
        Parts.Add(part);
    }

    void Clear()
    {
        foreach (var part in Parts) part.PropertyChanged -= OnPartChanged;
        Parts.Clear();
        Notes.Clear();
        Problem = null;
        PartsChanged();
    }

    void OnPartChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlainSearchPartViewModel.Clause)) PartsChanged();
    }

    void PartsChanged()
    {
        OnPropertyChanged(nameof(QueryText));
        OnPropertyChanged(nameof(HasParts));
        OnPropertyChanged(nameof(Message));
        SearchCommand.NotifyCanExecuteChanged();
    }
}
