using Bibliotaph.Api.Contracts;
using Bibliotaph.Processing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bibliotaph.App.ViewModels;

/// <summary>
/// Send to Nornis: pages of a book, as the reader names them, with a label and, when the book is in several, the world,
/// filed as an excerpt there. Bibliotaph only opens Nornis's page, which reads the pages and asks to confirm.
/// </summary>
public sealed partial class SendToNornisDialogViewModel(NornisSend send, string from, string to, string label, Func<string, int?> findPage) : ObservableObject
{
    /// <summary>Raised when the dialog closes: with Nornis's address to open, or null when it was cancelled.</summary>
    public event EventHandler<string?>? Closed;

    public IReadOnlyList<NornisSendWorld> Worlds { get; } = send.Worlds;

    /// <summary>The world picker shows only for a book in more than one world.</summary>
    public bool ChoosesWorld => Worlds.Count > 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PagesProblem))]
    [NotifyCanExecuteChangedFor(nameof(OpenCommand))]
    public partial string From { get; set; } = from;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PagesProblem))]
    [NotifyCanExecuteChangedFor(nameof(OpenCommand))]
    public partial string To { get; set; } = to;

    [ObservableProperty]
    public partial string Label { get; set; } = label;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenCommand))]
    public partial NornisSendWorld? World { get; set; } = send.Worlds.Count > 0 ? send.Worlds[0] : null;

    public string? PagesProblem => Range() is not { } range ? "Type pages as the book numbers them, or PDF page numbers."
        : range.Last - range.First + 1 > NornisSend.MostPages ? NornisSend.TooManyPages
        : null;

    (int First, int Last)? Range()
    {
        if (findPage(From) is not { } first) return null;
        var last = To.Trim().Length == 0 ? first : findPage(To);
        return last is { } l ? (Math.Min(first, l), Math.Max(first, l)) : null;
    }

    bool CanOpen => World is not null && PagesProblem is null;

    [RelayCommand(CanExecute = nameof(CanOpen))]
    void Open()
    {
        if (World is { } world && Range() is { } range) Closed?.Invoke(this, NornisSend.Excerpt(world, range.First, range.Last, Label));
    }

    [RelayCommand]
    void Cancel() => Closed?.Invoke(this, null);
}

/// <summary>Why the web library's answer to Send to Nornis opens no dialog, in the reader's and the binder's words.</summary>
public static class NornisSendText
{
    /// <summary>The words to show, or null when the answer offers a world to send to.</summary>
    public static string? Refusal(NornisSendAnswer? answer) => answer switch
    {
        null => "bibliotaph.online couldn't be reached. Try again in a moment.",
        { Send: null } => "This file isn't in your web library, so Nornis can't read its pages.",
        { Send.Worlds.Count: 0 } => "On bibliotaph.online this book isn't in a Nornis world's collection, or Nornis isn't connected.",
        { Send.Problem: { } problem } => problem,
        _ => null,
    };
}
