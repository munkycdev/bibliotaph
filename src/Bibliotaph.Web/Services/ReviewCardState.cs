using Bibliotaph.Api.Contracts;

namespace Bibliotaph.Web.Services;

/// <summary>
/// A Needs review card's decision, kept by the page rather than the card, so a decided card keeps its line and Undo
/// whatever the Show menu picks, until the page loads the list again.
/// </summary>
public sealed class ReviewCardState
{
    List<string> _undos = [];

    /// <summary>What deciding did, once decided: the card folds to this line.</summary>
    public string? Done { get; private set; }

    /// <summary>Whether the decision can be undone.</summary>
    public string? Undo => _undos.Count > 0 ? _undos[0] : null;

    public bool IsDone => Done is not null;

    public bool Busy { get; set; }

    /// <summary>Why the last try didn't work, shown on the card.</summary>
    public string? Problem { get; set; }

    /// <summary>
    /// The places a new version's card checked with Use this page, each with what undoes it, until every place is
    /// checked and the card's own Undo takes them all back.
    /// </summary>
    public Dictionary<string, string?> Places { get; } = new(StringComparer.Ordinal);

    public void MarkDone(ActionResult result) => MarkDone(result.Message, result.Undo is { } undo ? [undo] : []);

    public void MarkDone(string message, IEnumerable<string> undos)
    {
        Done = message;
        _undos = [.. undos];
        Problem = null;
    }

    /// <summary>
    /// Makes the decision, one at a time; <paramref name="decided"/> hears -1 when it's made, so the page counts what's
    /// waiting. What refused it stays on the card.
    /// </summary>
    public async Task<bool> DecideAsync(Func<Task<ActionResult>> decide, Func<int, Task> decided)
    {
        if (Busy) return false;
        Busy = true;
        try
        {
            MarkDone(await decide());
            await decided(-1);
            return true;
        }
        catch (ApiProblemException e)
        {
            Problem = e.Message;
            return false;
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>Undo: the card is back, and <paramref name="decided"/> hears +1. What couldn't be undone says so on it.</summary>
    public async Task UndoAsync(ApiClient api, Func<int, Task> decided)
    {
        if (_undos.Count == 0 || Busy) return;
        Busy = true;
        try
        {
            var problems = new List<string>();
            foreach (var token in _undos)
            {
                var result = await api.RunHeldAsync(token);
                if (result.Message.Length > 0) problems.Add(result.Message);
            }
            Done = null;
            _undos = [];
            Places.Clear();
            Problem = problems.Count > 0 ? problems[0] : null;
            await decided(+1);
        }
        catch (ApiProblemException e)
        {
            Problem = e.Message;
        }
        finally
        {
            Busy = false;
        }
    }
}
