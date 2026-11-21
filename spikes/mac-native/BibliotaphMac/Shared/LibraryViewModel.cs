using System.ComponentModel;
using System.Runtime.CompilerServices;
using Bibliotaph.Api.Contracts;

namespace Bibliotaph.MacSpike;

// Nothing in this folder names AppKit: it stands in for view models a shared Presentation project would hold.

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    protected void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public enum LibraryState { Starting, NeedsConnect, Connecting, Loading, Ready }

public sealed record BookCard(string Title, string Kind, string FolderName, string Location, string Path, string ContentHash,
    long SizeBytes, DateTime ModifiedUtc);

public sealed record FolderRow(long? Id, string Name, string Location, int Count);

public sealed class LibraryViewModel(WebShelfClient web, Action<Uri> openInBrowser) : Observable
{
    IReadOnlyList<BookCard> _all = [];

    public LibraryState State { get; private set => Set(ref field, value); } = LibraryState.Starting;

    /// <summary>Why the last attempt didn't work, in words for the connect panel; null when nothing went wrong.</summary>
    public string? Problem { get; private set => Set(ref field, value); }

    /// <summary>The code bibliotaph.online shows on its approval page, while this Mac waits to be approved.</summary>
    public string? ConnectCode { get; private set => Set(ref field, value); }

    public IReadOnlyList<FolderRow> Folders { get; private set => Set(ref field, value); } = [];

    public IReadOnlyList<BookCard> Books { get; private set => Set(ref field, value); } = [];

    public BookCard? SelectedBook { get; set => Set(ref field, value); }

    public long? SelectedFolderId
    {
        get;
        set { if (Set(ref field, value)) Refilter(); }
    }

    public string SearchText
    {
        get;
        set { if (Set(ref field, value)) Refilter(); }
    } = "";

    public string Subtitle => State == LibraryState.Ready ? $"{Books.Count:N0} of {_all.Count:N0} books" : "";

    public async Task StartAsync()
    {
        if (web.HasKey) await LoadAsync();
        else State = LibraryState.NeedsConnect;
    }

    public async Task ConnectAsync()
    {
        Problem = null;
        State = LibraryState.Connecting;
        try
        {
            if (await web.StartAsync($"{Environment.MachineName} (Mac)") is not { } started)
            {
                Fail("bibliotaph.online didn't take the request. Try again in a minute.");
                return;
            }
            ConnectCode = started.Code;
            openInBrowser(new Uri(started.ApproveUrl));
            if (await web.WaitAsync(started)) await LoadAsync();
            else Fail("The request was declined or ran out of time.");
        }
        catch (HttpRequestException)
        {
            Fail("bibliotaph.online couldn't be reached.");
        }
        finally
        {
            ConnectCode = null;
        }
    }

    public async Task DisconnectAsync()
    {
        await web.DisconnectAsync();
        _all = [];
        Folders = [];
        SelectedBook = null;
        Refilter();
        State = LibraryState.NeedsConnect;
    }

    public async Task LoadAsync()
    {
        State = LibraryState.Loading;
        WebShelf? shelf;
        try
        {
            shelf = await web.ReadShelfAsync();
        }
        catch (HttpRequestException)
        {
            shelf = null;
        }
        if (shelf is null)
        {
            Fail(web.HasKey ? "bibliotaph.online couldn't be reached." : "This Mac was disconnected. Connect it again.");
            return;
        }
        _all = [.. shelf.Folders.SelectMany(folder => folder.Files.Select(file => Card(folder, file)))
            .OrderBy(book => book.Title, StringComparer.CurrentCultureIgnoreCase)];
        Folders = [new FolderRow(null, "All books", "", _all.Count),
            .. shelf.Folders.Select(folder => new FolderRow(folder.Id, folder.Name, folder.Location, folder.Files.Count))];
        Refilter();
        State = LibraryState.Ready;
        Changed(nameof(Subtitle));
    }

    void Fail(string problem)
    {
        Problem = problem;
        State = LibraryState.NeedsConnect;
    }

    void Refilter()
    {
        var folder = SelectedFolderId is { } id ? Folders.FirstOrDefault(f => f.Id == id)?.Name : null;
        Books = [.. _all.Where(book => (folder is null || book.FolderName == folder)
            && (SearchText.Length == 0 || book.Title.Contains(SearchText, StringComparison.CurrentCultureIgnoreCase)))];
        if (SelectedBook is { } selected && !Books.Contains(selected)) SelectedBook = null;
        Changed(nameof(Subtitle));
    }

    static BookCard Card(WebShelfFolder folder, WebShelfFile file)
    {
        var name = file.Path[(file.Path.LastIndexOf('/') + 1)..];
        var title = System.IO.Path.GetFileNameWithoutExtension(name).Replace('_', ' ');
        return new BookCard(title, KindOf(name), folder.Name, folder.Location, file.Path, file.ContentHash, file.SizeBytes, file.ModifiedUtc);
    }

    static string KindOf(string name) => System.IO.Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".pdf" => "PDF",
        ".epub" => "EPUB",
        ".mobi" or ".azw3" => "Kindle",
        ".webm" => "Video",
        ".jpg" or ".jpeg" or ".png" or ".webp" => "Image",
        ".zip" => "ZIP",
        var other => other.TrimStart('.').ToUpperInvariant(),
    };
}
