using System.Text.Json;
using Bibliotaph.Core.Metadata;

namespace Bibliotaph.Processing.Tests;

/// <summary>Reading an online library's page (online libraries, O2 and O4), on made-up pages shaped like the real ones.</summary>
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
    public void A_library_read_page_by_page_builds_up_with_each_book_once()
    {
        var first = new OnlinePage([new("Ember Vault", "https://www.dndbeyond.com/sources/dnd/ember-vault")], 1, Complete: false);
        var second = new OnlinePage([new("Ember Vault", "https://www.dndbeyond.com/sources/dnd/EMBER-VAULT"), new("Atlas", "https://www.dndbeyond.com/sources/dnd/atlas")],
            0, Complete: true);

        var both = first.With(second);

        Assert.Equal(["Ember Vault", "Atlas"], both.Books.Select(b => b.Title));
        Assert.Equal((1, true), (both.Shared, both.Complete));
    }

    [Fact]
    public void A_library_with_nothing_owned_is_read_as_empty()
    {
        var page = new DndBeyondReader().Read(Page($$"""2e:{"sources":[{{Source(3, "Not Bought", owned: false)}}]}""" + "\n"));

        Assert.NotNull(page);
        Assert.Empty(page.Books);
    }

    static string Online(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "online", name));

    [Fact]
    public void A_demiplane_source_library_gives_the_books_unlocked_and_never_says_it_is_the_whole_library()
    {
        var page = new DemiplaneReader().Read(Online("demiplane-sources.html"));

        // The third card is only for sale.
        Assert.NotNull(page);
        Assert.False(page.Complete);
        Assert.Equal(
            [
                ("Ember Isles Core Rules", "https://app.demiplane.com/nexus/emberisles/sources/ember-isles-core", "./Source Library - Ember Isles RPG Nexus_files/cover.jpg"),
                ("Tides of Ash & Salt", "https://app.demiplane.com/nexus/emberisles/sources/tides-of-ash", "./Source Library - Ember Isles RPG Nexus_files/cover(1).jpg"),
            ],
            page.Books.Select(b => (b.Title, b.Link, b.CoverUrl)));
        Assert.Equal(MetadataFields.AlsoOwn, ((IOnlineLibraryReader)new DemiplaneReader()).LinkField);
    }

    [Fact]
    public void A_humble_bundle_library_is_read_with_ebook_chosen_and_each_book_linked_by_its_name()
    {
        IOnlineLibraryReader reader = new HumbleBundleReader();
        var html = Online("humble-library.html");

        var page = reader.Read(html);

        Assert.NotNull(page);
        Assert.True(page.Complete);
        Assert.Equal(MetadataFields.GetItFrom, reader.LinkField);
        Assert.Collection(page.Books,
            book =>
            {
                Assert.Equal(("Nine Crooked Lanterns", "https://www.humblebundle.com/home/library#nine-crooked-lanterns", "Lantern Hollow Games"), (book.Title, book.Link, book.Publisher));
                Assert.Equal("https://hb.imgix.net/0000000000000000000000000000000000000001.png?auto=compress,format&s=00000000000000000000000000000001", book.CoverUrl);
            },
            book => Assert.Equal(("The Clockwork Bestiary: Vol. 2", "Brass & Bone Press"), (book.Title, book.Publisher)));

        // With every platform chosen, the list holds games too, so it isn't read and the user is told what to choose.
        var all = html.Replace("<option value=\"all\">", "<option value=\"all\" selected=\"\">", StringComparison.Ordinal)
            .Replace("<option value=\"ebook\" selected=\"\">", "<option value=\"ebook\">", StringComparison.Ordinal);
        Assert.Null(reader.Read(all));
        Assert.Contains("eBook", reader.Hint(all), StringComparison.Ordinal);
        Assert.Null(reader.Hint(html));

        // A search typed in the library leaves books out, so the page isn't the whole library.
        var searched = html.Replace("id=\"search\" name=\"search\" class=\"js-search\"", "id=\"search\" name=\"search\" class=\"js-search\" value=\"lantern\"", StringComparison.Ordinal);
        Assert.False(reader.Read(searched)?.Complete);
    }

    [Fact]
    public void A_drivethrurpg_library_page_gives_its_books_with_their_publishers_and_isnt_the_whole_library_when_there_are_more_pages()
    {
        var reader = OneBookShelfReader.DriveThruRpg;
        var html = Online("drivethrurpg-library.html");

        var page = reader.Read(html);

        Assert.NotNull(page);
        Assert.False(page.Complete);
        Assert.Equal(
            [
                ("The Sunken Orrery", "https://www.drivethrurpg.com/en/product/900001/the-sunken-orrery", "Copper Kettle Games", "./DriveThruRPG - My Library_files/900001-thumb140.jpg"),
                ("Marsh Lights Quickstart", "https://www.drivethrurpg.com/en/product/900002/marsh-lights-quickstart", "Fenwick & Daughters", "./DriveThruRPG - My Library_files/900002-thumb140.jpg"),
            ],
            page.Books.Select(b => (b.Title, b.Link, b.Publisher, b.CoverUrl)));

        // A library on one page is the whole of it; DMs Guild's reader doesn't take DriveThruRPG's page.
        var start = html.IndexOf("<ngb-pagination>", StringComparison.Ordinal);
        Assert.True(reader.Read(html[..start] + "</body></html>")?.Complete);
        Assert.Null(OneBookShelfReader.DmsGuild.Read(html));
    }

    [Fact]
    public void Each_service_reads_only_its_own_library_page()
    {
        string[] pages = ["dndbeyond-library.html", "demiplane-sources.html", "humble-library.html", "drivethrurpg-library.html"];
        IOnlineLibraryReader[] readers = [new DndBeyondReader(), new DemiplaneReader(), new HumbleBundleReader(), OneBookShelfReader.DriveThruRpg];

        for (var i = 0; i < pages.Length; i++)
            for (var j = 0; j < readers.Length; j++)
                Assert.True((readers[j].Read(Online(pages[i])) is not null) == (i == j), $"{readers[j].Name} on {pages[i]}");
        Assert.Equal(["dnd-beyond", "demiplane", "bundle", "drivethrurpg", "dmsguild"],
            new IOnlineLibraryReader[] { new DndBeyondReader(), new DemiplaneReader(), new HumbleBundleReader(), OneBookShelfReader.DriveThruRpg, OneBookShelfReader.DmsGuild }
                .Select(r => r.Key));
    }

    [Fact]
    public async Task A_saved_page_replaces_the_file_chosen_and_leaves_nothing_beside_it()
    {
        var dir = Directory.CreateTempSubdirectory("bibliotaph-saved-page-").FullName;
        try
        {
            var path = Path.Combine(dir, "Store page.html");
            await File.WriteAllTextAsync(path, "an older copy");

            await SavedPages.SaveAsync(path, "<html><body>Owned</body></html>");

            Assert.Equal("<!DOCTYPE html>\n<html><body>Owned</body></html>", await File.ReadAllTextAsync(path));
            Assert.Equal([path], Directory.GetFiles(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
