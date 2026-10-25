namespace Bibliotaph.Web.Services;

/// <summary>A collection in a picker: indented under the one it sits in, or listed by its path while filtering.</summary>
public sealed record CollectionChoice(long? Id, string Name, string Path, int Depth);

/// <summary>
/// The dialog over a page for collections, as the Windows app's: naming one (New collection, Rename), or picking one
/// from all of them (Add to another collection, Move to). A page awaits the answer; the shell draws the dialog.
/// </summary>
public sealed class CollectionDialogs
{
    TaskCompletionSource<object?>? _answer;

    /// <summary>The dialog open now, if any.</summary>
    public CollectionDialogRequest? Current { get; private set; }

    public event Action? Changed;

    /// <summary>A name and an optional description, filled in for Rename; null when cancelled.</summary>
    public async Task<(string Name, string? Description)?> NameAsync(string heading, string actionText, string name = "", string? description = null) =>
        await OpenAsync(new CollectionDialogRequest("COLLECTION", heading, actionText, true, [], name, description ?? "")) as (string, string?)?;

    /// <summary>Which collection, from <paramref name="choices"/> in tree order; null when cancelled.</summary>
    public async Task<CollectionChoice?> PickAsync(string heading, IReadOnlyList<CollectionChoice> choices) =>
        await OpenAsync(new CollectionDialogRequest("COLLECTIONS", heading, "", false, choices, "", "")) as CollectionChoice;

    /// <summary>What the dialog closed with: a name, a choice, or null for Cancel and Esc.</summary>
    public void Close(object? answer)
    {
        var pending = _answer;
        _answer = null;
        Current = null;
        Changed?.Invoke();
        pending?.TrySetResult(answer);
    }

    Task<object?> OpenAsync(CollectionDialogRequest request)
    {
        // One at a time: a second replaces the first, which closes as cancelled.
        Close(null);
        _answer = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Current = request;
        Changed?.Invoke();
        return _answer.Task;
    }
}

public sealed record CollectionDialogRequest(string Eyebrow, string Heading, string ActionText, bool Naming, IReadOnlyList<CollectionChoice> Choices,
    string Name, string Description);
