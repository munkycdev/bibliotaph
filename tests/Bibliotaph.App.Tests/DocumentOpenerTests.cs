using Bibliotaph.App.Services;
using Bibliotaph.Pdf.Contracts;
using Bibliotaph.Viewer;

namespace Bibliotaph.App.Tests;

/// <summary>
/// Opening a PDF for the reader: the password from this sitting or a remembered one first, then asking, again when
/// what was typed is wrong; the problems shown when it won't open; and the password kept once it has.
/// </summary>
public class DocumentOpenerTests
{
    const string Hash = "hash-sunken-bell";
    const string Title = "The Sunken Bell";
    const string Right = "bell";

    /// <summary>Remembered passwords, in memory; <see cref="Stores"/> false is Windows refusing to keep one.</summary>
    sealed class FakeVault : IPasswordVault
    {
        public Dictionary<string, string> Passwords { get; } = [];

        public bool Stores { get; set; } = true;

        public string? Find(string contentHash) => Passwords.GetValueOrDefault(contentHash);

        public bool Remember(string contentHash, string password)
        {
            if (!Stores) return false;
            Passwords[contentHash] = password;
            return true;
        }

        public void Forget(string contentHash) => Passwords.Remove(contentHash);

        public int ForgetAll()
        {
            var count = Passwords.Count;
            Passwords.Clear();
            return count;
        }

        public IReadOnlyList<string> List() => [.. Passwords.Keys];
    }

    /// <summary>The reader at the password prompt: each answer in turn, then Cancel.</summary>
    sealed class FakePrompt : IPasswordPrompt
    {
        readonly Queue<EnteredPassword?> _answers = new();

        public List<bool> Asked { get; } = [];

        public void Answer(params EnteredPassword?[] answers)
        {
            foreach (var answer in answers) _answers.Enqueue(answer);
        }

        public EnteredPassword? Ask(string title, bool retry)
        {
            Assert.Equal(Title, title);
            Asked.Add(retry);
            return _answers.Count > 0 ? _answers.Dequeue() : null;
        }
    }

    readonly UnlockedPasswords _unlocked = new();
    readonly FakeVault _vault = new();
    readonly FakePrompt _prompt = new();
    readonly DocumentOpener _opener;
    readonly List<string?> _tried = [];
    ErrorKind? _fails;
    bool _locked = true;
    bool _wanted = true;

    public DocumentOpenerTests() => _opener = new DocumentOpener(_unlocked, _vault, _prompt);

    /// <summary>The worker opening the book: it fails as told, or wants <see cref="Right"/> while the book is locked.</summary>
    Task<DocInfo> Open(string? password)
    {
        _tried.Add(password);
        if (_fails is { } kind) return Task.FromException<DocInfo>(new PdfOpenException(kind, "It wouldn't open."));
        if (_locked && password != Right) return Task.FromException<DocInfo>(new PdfOpenException(ErrorKind.Password, "Wrong password."));
        return Task.FromResult(new DocInfo { PageCount = 12 });
    }

    Task<OpenResult> OpenAsync() => _opener.OpenAsync(Hash, Title, Open, () => _wanted);

    [Fact]
    public async Task A_book_without_a_password_opens_without_asking()
    {
        _locked = false;

        var result = await OpenAsync();

        Assert.Equal(12, result.Document?.Doc.PageCount);
        Assert.Null(result.Document?.Password);
        Assert.Null(result.Problem);
        Assert.Equal([null], _tried);
        Assert.Empty(_prompt.Asked);
    }

    [Fact]
    public async Task A_remembered_password_is_tried_first_and_nobody_is_asked()
    {
        _vault.Passwords[Hash] = Right;

        var result = await OpenAsync();

        Assert.Equal(Right, result.Document?.Password);
        Assert.Null(result.Document?.Entered);
        Assert.Equal([Right], _tried);
        Assert.Empty(_prompt.Asked);
    }

    [Fact]
    public async Task The_password_from_this_sitting_comes_before_a_remembered_one()
    {
        _unlocked.Add(Hash, Right);
        _vault.Passwords[Hash] = "old";

        var result = await OpenAsync();

        Assert.Equal(Right, result.Document?.Password);
        Assert.Equal([Right], _tried);
    }

    [Fact]
    public async Task A_remembered_password_that_no_longer_works_is_forgotten_and_the_reader_is_asked()
    {
        _vault.Passwords[Hash] = "old";
        _prompt.Answer(new EnteredPassword(Right, Remember: false));

        var result = await OpenAsync();

        Assert.Equal(Right, result.Document?.Password);
        Assert.False(_vault.Passwords.ContainsKey(Hash));
        Assert.Equal(["old", Right], _tried);
        Assert.Equal([false], _prompt.Asked);
    }

    [Fact]
    public async Task A_password_from_this_sitting_that_no_longer_works_is_forgotten()
    {
        _unlocked.Add(Hash, "old");
        _prompt.Answer(new EnteredPassword(Right, Remember: false));

        var result = await OpenAsync();

        Assert.Equal(Right, result.Document?.Password);
        Assert.Null(_unlocked.Find(Hash));
    }

    [Fact]
    public async Task A_wrong_password_asks_again_saying_it_was_wrong()
    {
        _prompt.Answer(new EnteredPassword("bel", Remember: false), new EnteredPassword(Right, Remember: true));

        var result = await OpenAsync();

        Assert.Equal(Right, result.Document?.Password);
        Assert.Equal(new EnteredPassword(Right, Remember: true), result.Document?.Entered);
        Assert.Equal([null, "bel", Right], _tried);
        Assert.Equal([false, true], _prompt.Asked);
    }

    [Fact]
    public async Task Cancelling_the_prompt_shows_that_the_book_needs_its_password()
    {
        var result = await OpenAsync();

        Assert.Null(result.Document);
        Assert.Equal(new OpenProblem("This PDF needs a password.", "Enter its password to read it, and to make its text searchable.", "Enter password"), result.Problem);
        Assert.Equal([false], _prompt.Asked);
    }

    [Fact]
    public async Task Once_the_reader_has_moved_on_nobody_is_asked_and_nothing_is_shown()
    {
        _wanted = false;

        var result = await OpenAsync();

        Assert.Same(OpenResult.MovedOn, result);
        Assert.Empty(_prompt.Asked);
    }

    [Fact]
    public async Task A_protected_pdf_offers_another_app_and_asks_for_no_password()
    {
        _fails = ErrorKind.Security;

        var result = await OpenAsync();

        Assert.Equal("Can't be read here.", result.Problem?.Title);
        Assert.Equal("Open in another app", result.Problem?.Action);
        Assert.True(result.Problem?.OpenElsewhere);
        Assert.Empty(_prompt.Asked);
    }

    [Theory]
    [InlineData(ErrorKind.Format, "This file isn't a readable PDF.", "It may be damaged, or only partly downloaded.")]
    [InlineData(ErrorKind.File, "The file could not be read.", "It may be in use, or still downloading.")]
    [InlineData(ErrorKind.Internal, "This book could not be opened.", "It wouldn't open.")]
    public async Task A_file_that_wont_open_says_why_with_Try_again(ErrorKind kind, string title, string message)
    {
        _fails = kind;

        var result = await OpenAsync();

        Assert.Equal(new OpenProblem(title, message), result.Problem);
        Assert.Equal("Try again", result.Problem?.Action);
        Assert.False(result.Problem?.OpenElsewhere);
    }

    [Fact]
    public async Task A_typed_password_is_kept_for_this_sitting_lent_to_indexing_and_remembered_when_asked()
    {
        _prompt.Answer(new EnteredPassword(Right, Remember: true, Searchable: true));
        var opened = (await OpenAsync()).Document!;

        var kept = _opener.Keep(Hash, opened);

        Assert.True(kept);
        Assert.True(opened.UnlocksIndexing);
        Assert.Equal(Right, _unlocked.FindForIndexing(Hash));
        Assert.Equal(Right, _vault.Find(Hash));
        Assert.Equal(Right, _opener.KnownPassword(Hash));
    }

    [Fact]
    public async Task A_password_typed_only_for_reading_is_kept_but_not_lent_to_indexing()
    {
        _prompt.Answer(new EnteredPassword(Right, Remember: false, Searchable: false));
        var opened = (await OpenAsync()).Document!;

        Assert.True(_opener.Keep(Hash, opened));

        Assert.False(opened.UnlocksIndexing);
        Assert.Equal(Right, _unlocked.Find(Hash));
        Assert.Null(_unlocked.FindForIndexing(Hash));
        Assert.Null(_vault.Find(Hash));
    }

    [Fact]
    public async Task Keeping_says_so_when_Windows_wouldnt_remember_the_password()
    {
        _vault.Stores = false;
        _prompt.Answer(new EnteredPassword(Right, Remember: true));
        var opened = (await OpenAsync()).Document!;

        Assert.False(_opener.Keep(Hash, opened));
        Assert.Equal(Right, _unlocked.Find(Hash));
    }
}
