using System.Globalization;
using Bibliotaph.Api.Contracts;

namespace Bibliotaph.Web.Services;

/// <summary>Ask this book, and Ask in the top bar.</summary>
public sealed class AskClient(ApiHttp http)
{
    /// <summary>Ask this book as the reader opens it, which also starts its pages being read for meaning.</summary>
    public Task<AskBook> AskBookAsync(long documentId, CancellationToken ct = default) => http.SendAsync<AskBook>(HttpMethod.Get, Ask(documentId), null, ct);

    /// <summary>New conversation: forgets the book's questions and answers so far.</summary>
    public Task NewAskConversationAsync(long documentId, CancellationToken ct = default) => http.SendAsync(HttpMethod.Delete, Ask(documentId), null, ct);

    /// <summary>
    /// Asks the book a question, yielding each change to the panel as the server finds the pages and the model writes
    /// its answer. Cancelling <paramref name="ct"/> stops the model.
    /// </summary>
    public IAsyncEnumerable<AskUpdate> AskAsync(long documentId, string question, CancellationToken ct = default) =>
        http.StreamAsync<AskUpdate>(Ask(documentId), new AskQuestion(question), ct);

    /// <summary>Whether Ask in the top bar can read a request or answer a question: AI is set up and on.</summary>
    public Task<AskStatus> AskStatusAsync(CancellationToken ct = default) => http.SendAsync<AskStatus>(HttpMethod.Get, "api/ask", null, ct);

    /// <summary>Reads a request in plain English as the search box's fields and words. Cancelling stops the model.</summary>
    public Task<AskReading> ReadRequestAsync(string request, CancellationToken ct = default) =>
        http.SendAsync<AskReading>(HttpMethod.Post, "api/ask/search", new AskQuestion(request), ct);

    /// <summary>
    /// Asks the books in <paramref name="scope"/>, the Library's filters and search fields, a question, yielding each
    /// change to the panel as the server finds the pages and the model writes its answer. Cancelling stops the model.
    /// </summary>
    public IAsyncEnumerable<AskUpdate> AskLibraryAsync(string question, LibraryQuery scope, CancellationToken ct = default) =>
        http.StreamAsync<AskUpdate>("api/ask/library", new AskLibraryQuestion(question, scope), ct);

    static string Ask(long documentId) => "api/ask/" + documentId.ToString(CultureInfo.InvariantCulture);
}
