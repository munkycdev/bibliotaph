using Bibliotaph.Api.Contracts;

namespace Bibliotaph.Web.Services;

/// <summary>
/// A Needs review card's decision, kept by the page rather than the card, so a decided card keeps its line and Undo
/// whatever the Show menu picks, until the page loads the list again.
/// </summary>
public sealed class ReviewCardState
{
    /// <summary>What deciding did, once decided: the card folds to this line.</summary>
    public string? Done { get; private set; }

    public string? Undo { get; private set; }

    public bool IsDone => Done is not null;

    public bool Busy { get; set; }

    /// <summary>Why the last try didn't work, shown on the card.</summary>
    public string? Problem { get; set; }

    public void MarkDone(ActionResult result)
    {
        Done = result.Message;
        Undo = result.Undo;
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
        if (Undo is not { } token || Busy) return;
        Busy = true;
        try
        {
            var result = await api.RunHeldAsync(token);
            Done = null;
            Undo = null;
            Problem = result.Message.Length > 0 ? result.Message : null;
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
