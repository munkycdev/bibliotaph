using Bibliotaph.Api.Contracts;

namespace Bibliotaph.Web.Components.Details;

/// <summary>What a section of a book's details does through the details around it.</summary>
public interface IDetailsHost
{
    /// <summary>The book whose details these are.</summary>
    long EntryId { get; }

    /// <summary>Runs a change, showing what the server said when it refused; null then.</summary>
    Task<T?> TryAsync<T>(Func<Task<T>> change) where T : class;

    /// <summary>The details as a change left them.</summary>
    void Replace(BookDetails details);

    /// <summary>Reads the details again.</summary>
    Task RefreshAsync();

    /// <summary>After a change the library's cards may show, so they can be read again.</summary>
    Task ChangedAsync();

    /// <summary>A change whose note belongs to the library, which shows it with its Undo.</summary>
    Task DoneAsync(ActionResult result);

    /// <summary>Another book's details, such as an adventure's file.</summary>
    Task ShowAsync(long entryId);

    /// <summary>A note the next book's details show, as the file an adventure was removed from.</summary>
    void Carry(ActionResult note);

    Task CloseAsync();
}

/// <summary>How a book's fields are shown: all of them or the known ones, and whose evidence is open.</summary>
public sealed class FieldsView
{
    public bool All { get; set; }
    public HashSet<string> Open { get; } = [];

    public void Toggle(string key)
    {
        if (!Open.Remove(key)) Open.Add(key);
    }
}
