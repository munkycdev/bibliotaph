namespace Bibliotaph.Web.Services;

/// <summary>A collection in a picker: indented under the one it sits in, or listed by its path while filtering.</summary>
public sealed record CollectionChoice(long? Id, string Name, string Path, int Depth);

/// <summary>
/// The dialog over a page for collections and saved searches, as the Windows app's: naming one (New collection, Save
/// search, Rename), or picking a collection from all of them (Add to another collection, Move to). A page awaits the
/// answer; the shell draws the dialog.
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

    /// <summary>A saved search's name, filled in for Rename and Save as new; null when cancelled.</summary>
    public async Task<string?> NameSearchAsync(string heading, string actionText, string name = "") =>
        await OpenAsync(new CollectionDialogRequest("SAVED SEARCH", heading, actionText, true, [], name, "", Describe: false,
            Intro: "It keeps this search, these filters and this order, and runs them again each time, so new books show up in it.")) is ValueTuple<string, string?> named
            ? named.Item1 : null;

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

/// <summary>A dialog to show. <see cref="Describe"/> asks for a description too; <see cref="Intro"/> says what is being named.</summary>
public sealed record CollectionDialogRequest(string Eyebrow, string Heading, string ActionText, bool Naming, IReadOnlyList<CollectionChoice> Choices,
    string Name, string Description, bool Describe = true, string? Intro = null);
