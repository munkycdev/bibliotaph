using Bibliotaph.Pdf.Contracts;
using Bibliotaph.Viewer;

namespace Bibliotaph.App.Services;

/// <summary>
/// Why a book didn't open, as the reader shows it: what happened, what to do, and the words on its button (null for
/// none). <see cref="OpenElsewhere"/> makes the button Open in another app rather than Try again.
/// </summary>
public sealed record OpenProblem(string Title, string Message, string? Action = "Try again", bool OpenElsewhere = false);

/// <summary>
/// A PDF that opened, with the password that opened it if it needed one, and what the reader typed when they were
/// asked for it.
/// </summary>
public sealed record OpenedDocument(DocInfo Doc, string? Password, EnteredPassword? Entered)
{
    /// <summary>The reader let indexing use the password, so the book's text can be read for search now.</summary>
    public bool UnlocksIndexing => Entered is { Searchable: true } or { Remember: true };
}

/// <summary>What came of opening a PDF: the document, or the problem to show. Neither when the reader moved on meanwhile.</summary>
public sealed record OpenResult(OpenedDocument? Document, OpenProblem? Problem)
{
    public static OpenResult MovedOn { get; } = new(null, null);
}

/// <summary>
/// Opens a PDF for reading, password and all: it tries the password that opened the book earlier in this sitting or
/// a remembered one first, forgets one that no longer works, and then asks the reader, again if what they typed was
/// wrong. Once the book is open it keeps the password for this sitting, and remembers it when the reader asked.
/// </summary>
public sealed class DocumentOpener(UnlockedPasswords unlocked, IPasswordVault vault, IPasswordPrompt prompt)
{
    /// <summary>The password to try first, without asking: one that opened the book in this sitting, else a remembered one.</summary>
    public string? KnownPassword(string contentHash) => unlocked.Find(contentHash) ?? vault.Find(contentHash);

    /// <summary>
    /// Opens the book with <paramref name="open"/>, which takes the password to try and throws
    /// <see cref="PdfOpenException"/> when the book won't open with it.
    /// </summary>
    /// <param name="contentHash">The book's content hash, which its passwords are kept under.</param>
    /// <param name="title">The book's title, for the password prompt.</param>
    /// <param name="open">Opens the PDF with a password, or none.</param>
    /// <param name="wanted">False once the reader has moved on to something else, so nobody is asked for a password.</param>
    public async Task<OpenResult> OpenAsync(string contentHash, string title, Func<string?, Task<DocInfo>> open, Func<bool> wanted)
    {
        var known = unlocked.Find(contentHash);
        var password = known ?? vault.Find(contentHash);
        var remembered = known is null && password is not null;
        EnteredPassword? entered = null;
        var asked = 0;
        while (true)
        {
            try
            {
                return new OpenResult(new OpenedDocument(await open(password), password, entered), null);
            }
            catch (PdfOpenException ex) when (ex.Kind == ErrorKind.Password)
            {
                if (!wanted()) return OpenResult.MovedOn;
                if (known is not null)
                {
                    unlocked.Forget(contentHash);
                    known = null;
                }
                else if (remembered)
                {
                    // The book changed its password, or a different file now has this content hash's name.
                    vault.Forget(contentHash);
                    remembered = false;
                }
                entered = prompt.Ask(title, retry: asked++ > 0);
                if (entered is null)
                    return new OpenResult(null, new OpenProblem("This PDF needs a password.", "Enter its password to read it, and to make its text searchable.", "Enter password"));
                password = entered.Password;
            }
            catch (PdfOpenException ex) when (ex.Kind == ErrorKind.Security)
            {
                // DRM: no password helps, and nor does trying again.
                return new OpenResult(null, new OpenProblem("Can't be read here.", "This PDF uses a protection scheme Bibliotaph can't open. Its details stay editable, " +
                    "and your default PDF app may be able to open it.", "Open in another app", OpenElsewhere: true));
            }
            catch (PdfOpenException ex)
            {
                var (problem, message) = Failure(ex);
                return new OpenResult(null, new OpenProblem(problem, message));
            }
        }
    }

    /// <summary>
    /// Keeps the password that opened the book in memory for this sitting, so the book moving to another window isn't
    /// asked for again; with "Make its text searchable" ticked, indexing may use it too until the app closes. Remembers
    /// it when the reader asked. False when Windows didn't store a password the reader asked to remember.
    /// </summary>
    public bool Keep(string contentHash, OpenedDocument opened)
    {
        if (opened.Password is not { } password) return true;
        unlocked.Add(contentHash, password, forIndexing: opened.Entered?.Searchable == true);
        return opened.Entered is not { Remember: true } || vault.Remember(contentHash, password);
    }

    static (string Title, string Message) Failure(PdfOpenException ex) => ex.Kind switch
    {
        ErrorKind.Format => ("This file isn't a readable PDF.", "It may be damaged, or only partly downloaded."),
        ErrorKind.File => ("The file could not be read.", "It may be in use, or still downloading."),
        _ => ("This book could not be opened.", ex.Message),
    };
}
