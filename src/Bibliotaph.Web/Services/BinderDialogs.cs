using Bibliotaph.Api.Contracts;

namespace Bibliotaph.Web.Services;

public enum BinderDialogKind
{
    /// <summary>A title and an optional date, or kept at hand for a game system: New binder, Edit title and date.</summary>
    Binder,

    /// <summary>Just a name: a section.</summary>
    Section,

    /// <summary>Which binder, from all of them: Add to another binder.</summary>
    Pick,
}

/// <summary>A binder in the picker.</summary>
public sealed record BinderChoice(long Id, string Title);

/// <summary>A dialog to show, with what it starts with.</summary>
public sealed record BinderDialogRequest(BinderDialogKind Kind, string Heading, string ActionText, BinderEdit Start, IReadOnlyList<FilterChoice> Systems,
    IReadOnlyList<BinderChoice> Choices)
{
    public string Eyebrow => Kind == BinderDialogKind.Section ? "SECTION" : "BINDER";
}

/// <summary>
/// The dialog over a page for binders, as the Windows app's session dialog: naming a binder (New binder, Edit title
/// and date), naming a section, or picking a binder. A page awaits the answer; the shell draws the dialog.
/// </summary>
public sealed class BinderDialogs
{
    TaskCompletionSource<object?>? _answer;

    public BinderDialogRequest? Current { get; private set; }

    public event Action? Changed;

    /// <summary>A title and date, or kept at hand for a game system, filled in for Edit; null when cancelled.</summary>
    public async Task<BinderEdit?> NameAsync(string heading, string actionText, IReadOnlyList<FilterChoice> systems, BinderEdit? start = null) =>
        await OpenAsync(new BinderDialogRequest(BinderDialogKind.Binder, heading, actionText, start ?? new BinderEdit("", null, false, null), systems, [])) as BinderEdit;

    /// <summary>A section's name, filled in for Rename; null when cancelled.</summary>
    public async Task<string?> NameSectionAsync(string heading, string actionText, string name = "") =>
        await OpenAsync(new BinderDialogRequest(BinderDialogKind.Section, heading, actionText, new BinderEdit(name, null, false, null), [], [])) as BinderEdit is { } named
            ? named.Title : null;

    /// <summary>Which binder, from <paramref name="choices"/>; null when cancelled.</summary>
    public async Task<BinderChoice?> PickAsync(string heading, IReadOnlyList<BinderChoice> choices) =>
        await OpenAsync(new BinderDialogRequest(BinderDialogKind.Pick, heading, "", new BinderEdit("", null, false, null), [], choices)) as BinderChoice;

    /// <summary>What the dialog closed with: a binder's title and date, a choice, or null for Cancel and Esc.</summary>
    public void Close(object? answer)
    {
        var pending = _answer;
        _answer = null;
        Current = null;
        Changed?.Invoke();
        pending?.TrySetResult(answer);
    }

    Task<object?> OpenAsync(BinderDialogRequest request)
    {
        // One at a time: a second replaces the first, which closes as cancelled.
        Close(null);
        _answer = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Current = request;
        Changed?.Invoke();
        return _answer.Task;
    }
}
