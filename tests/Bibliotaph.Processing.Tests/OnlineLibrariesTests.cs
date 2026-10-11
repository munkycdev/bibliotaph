using System.Text.Json;

namespace Bibliotaph.Processing.Tests;

/// <summary>Reading an online library's page (online libraries, O2): D&amp;D Beyond's, on made-up pages shaped like the real one.</summary>
public sealed class OnlineLibrariesTests
{
    static readonly string Fixture = Path.Combine(AppContext.BaseDirectory, "online", "dndbeyond-library.html");

    /// <summary>A page with these Next.js data chunks, as the real page carries them.</summary>
    static string Page(params string[] chunks) =>
        "<html><head><title>My Library | Dungeons &amp; Dragons</title></head><body><a href=\"https://www.dndbeyond.com/\">D&amp;D Beyond</a>"
        + string.Concat(chunks.Select(c => $"<script>self.__next_f.push([1,{JsonSerializer.Serialize(c)}])</script>")) + "</body></html>";

    static string Source(int id, string name, bool owned = true, bool shared = false) =>
        $$"""{"id":{{id}},"name":"{{name}}","relativePath":"/sources/dnd/book-{{id}}","publisher":"Gilded Press","type":"adventure","isOwned":{{(owned ? "true" : "false")}},"isSharedWithMe":{{(shared ? "true" : "false")}}}""";

    [Fact]
    public void A_saved_library_page_gives_the_books_owned_and_counts_those_shared()
    {
        var page = new DndBeyondReader().Read(File.ReadAllText(Fixture));

        Assert.NotNull(page);
        Assert.Equal((1, true), (page.Shared, page.Complete));
        Assert.Collection(page.Books,
            book =>
            {
                Assert.Equal(("The Ember Vault", "https://www.dndbeyond.com/sources/dnd/ember-vault"), (book.Title, book.Link));
                Assert.Equal(("Gilded Press", "adventure", "D&D", "5e", 2019), (book.Publisher, book.Type, book.System, book.Edition, book.Year));
                Assert.Equal("https://www.dndbeyond.com/avatars/900/901/639000000000000001.jpeg", book.CoverUrl);
            },
            // Its object is split across two of the page's scripts.
            book => Assert.Equal(("Atlas of the Drowned Coast", "sourcebook", "5.5e", 2025), (book.Title, book.Type, book.Edition, book.Year)));
    }

    [Fact]
    public void A_book_listed_twice_is_read_once_and_one_both_owned_and_shared_is_owned()
    {
        var list = $$"""2e:["$","$L33",null,{"sources":[{{Source(1, "Ember Vault")}},{{Source(2, "Lent Tome", owned: false, shared: true)}}]}]""";
        var again = $$"""3f:["$","$L34",null,{"sources":[{{Source(1, "Ember Vault", shared: true)}},{{Source(3, "Not Bought", owned: false)}}]}]""";

        var page = new DndBeyondReader().Read(Page(list + "\n", again + "\n"));

        Assert.NotNull(page);
        Assert.Equal(["Ember Vault"], page.Books.Select(b => b.Title));
        Assert.Equal(1, page.Shared);
    }

    [Fact]
    public void A_page_without_the_library_isnt_read()
    {
        var reader = new DndBeyondReader();

        // Another site's page, D&D Beyond's sign-in page, and a page cut short before its list ends.
        Assert.Null(reader.Read("<html><body><script>self.__next_f.push([1,\"sources\"])</script></body></html>"));
        Assert.Null(reader.Read(Page("""0:{"P":null,"b":"sign-in"}""" + "\n")));
        Assert.Null(reader.Read(Page($$"""2e:["$","$L33",null,{"sources":[{{Source(1, "Ember Vault")}},""")));
    }

    [Fact]
    public void A_library_with_nothing_owned_is_read_as_empty()
    {
        var page = new DndBeyondReader().Read(Page($$"""2e:{"sources":[{{Source(3, "Not Bought", owned: false)}}]}""" + "\n"));

        Assert.NotNull(page);
        Assert.Empty(page.Books);
    }
}
