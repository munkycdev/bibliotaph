using Bibliotaph.Api.Contracts;
using Bibliotaph.Web.Services;

namespace Bibliotaph.Web.Components.Reader;

/// <summary>
/// The passwords one opening of a PDF tries: one that opened it in this tab, or else a remembered one, first; then
/// what the reader types, again while it doesn't open it. A known password that fails is forgotten, not reported.
/// </summary>
public sealed class ReaderPasswords(ReaderClient readerApi, UnlockedBooks unlocked)
{
    bool _knownTried;
    (string Password, bool Remembered)? _tryingKnown;
    ReaderPasswordUse? _entered;

    /// <summary>The dialog's ticks for the ask in hand.</summary>
    public PasswordTicks Ticks { get; private set; } = new();

    /// <summary>A new opening, which tries the known password first again.</summary>
    public void Reset()
    {
        _knownTried = false;
        _tryingKnown = null;
        _entered = null;
    }

    /// <summary>
    /// A password known for the book, to try before asking; or none, with whether the ask is after a wrong password.
    /// </summary>
    public async Task<(string? Known, bool Retry)> BeforeAskingAsync(ReaderBook book, bool retry)
    {
        if (_tryingKnown is { } failed)
        {
            // The book changed its password, or a different file now has this content.
            _tryingKnown = null;
            unlocked.Forget(book.ContentHash);
            if (failed.Remembered) _ = ForgetRememberedAsync(book.DocumentId);
            retry = false;
        }
        else if (!_knownTried)
        {
            _knownTried = true;
            var known = unlocked.Find(book.ContentHash);
            var remembered = known is null ? await RememberedAsync(book.DocumentId) : null;
            if ((known ?? remembered) is { } password)
            {
                _tryingKnown = (password, known is null);
                return (password, retry);
            }
        }
        // A wrong password keeps the ticks; a new ask starts.
        if (!retry) Ticks = new PasswordTicks();
        return (null, retry);
    }

    /// <summary>What the reader typed, or null for none, kept with the ticks until the book opens with it.</summary>
    public void Typed(string? typed) => _entered = typed is null ? null : new ReaderPasswordUse(typed, Ticks.Remember, Ticks.MakesSearchable);

    /// <summary>
    /// The book opened. A password the reader typed is kept for this tab and, as ticked, sent for indexing to read the
    /// book with and to remember; what to tell the reader when that failed, or null.
    /// </summary>
    public async Task<string?> OpenedAsync(ReaderBook book)
    {
        _tryingKnown = null;
        if (_entered is not { } use) return null;
        _entered = null;
        unlocked.Add(book.ContentHash, use.Password);
        if (!use.Remember && !use.Searchable) return null;
        try
        {
            await readerApi.UsePasswordAsync(book.DocumentId, use);
            return null;
        }
        catch (Exception e) when (e is ApiProblemException or HttpRequestException)
        {
            return use.Remember ? "Bibliotaph couldn't remember the password, so it will ask for it next time."
                : "Bibliotaph couldn't make the book searchable just now. Open it again later to try again.";
        }
    }

    async Task<string?> RememberedAsync(long documentId)
    {
        try
        {
            return (await readerApi.RememberedPasswordAsync(documentId)).Password;
        }
        catch (Exception e) when (e is ApiProblemException or HttpRequestException)
        {
            return null;
        }
    }

    async Task ForgetRememberedAsync(long documentId)
    {
        try
        {
            await readerApi.ForgetPasswordAsync(documentId);
        }
        catch (Exception e) when (e is ApiProblemException or HttpRequestException)
        {
            // Tried and forgotten again at the next open.
        }
    }
}
