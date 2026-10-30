using Bibliotaph.Server.Site;

namespace Bibliotaph.Server.Tests;

public sealed class SiteMarkdownTests
{
    [Fact]
    public void The_change_log_reads_as_headings_and_lists_with_links_to_the_repository()
    {
        const string markdown = """
            # Changes

            ## 1.3 · 19 October 2026

            - **Weekly backups.** Kept in `backups`, four at a time. See
              [Backup](docs/guide/web.md#backup) or <script>alert(1)</script>.
            - *Faster* search, from https://example.com/a.

            A paragraph
            over two lines.
            """;

        var html = SiteMarkdown.ToHtml(markdown);

        Assert.Equal("""
            <h2 id="1-3-19-october-2026">1.3 · 19 October 2026</h2>
            <ul>
            <li><strong>Weekly backups.</strong> Kept in <code>backups</code>, four at a time. See <a href="https://github.com/munkycdev/bibliotaph/blob/main/docs/guide/web.md#backup">Backup</a> or &lt;script&gt;alert(1)&lt;/script&gt;.</li>
            <li><em>Faster</em> search, from <a href="https://example.com/a">https://example.com/a</a>.</li>
            </ul>
            <p>A paragraph over two lines.</p>

            """.Replace("\r\n", "\n", StringComparison.Ordinal), html);
    }

    [Fact]
    public void A_table_keeps_its_header_and_rows_and_links_bare_addresses()
    {
        const string markdown = """
            | Component | Licence | Project |
            | --- | --- | --- |
            | Dapper | Apache-2.0 | https://github.com/DapperLib/Dapper |
            """;

        var html = SiteMarkdown.ToHtml(markdown);

        Assert.Contains("<thead><tr><th>Component</th><th>Licence</th><th>Project</th></tr></thead>", html, StringComparison.Ordinal);
        Assert.Contains("""<tr><td>Dapper</td><td>Apache-2.0</td><td><a href="https://github.com/DapperLib/Dapper">https://github.com/DapperLib/Dapper</a></td></tr>""",
            html, StringComparison.Ordinal);
    }

    [Fact]
    public void Numbered_steps_keep_their_indented_lines_paragraphs_and_lists()
    {
        const string markdown = """
            1. Open **Settings**.
            2. Pick a model. Those that fit
               come first.

               Already have one? Use it.
            3. Click **Read this page**:
               - **New**: books that get a card
                 of their own.
               - **Imported before**: nothing.

            ---

            Next: [Searching](searching.md)
            """;

        var html = SiteMarkdown.ToHtml(markdown);

        Assert.Equal("""
            <ol>
            <li>Open <strong>Settings</strong>.</li>
            <li><p>Pick a model. Those that fit come first.</p>
            <p>Already have one? Use it.</p></li>
            <li>Click <strong>Read this page</strong>:<ul>
            <li><strong>New</strong>: books that get a card of their own.</li>
            <li><strong>Imported before</strong>: nothing.</li>
            </ul></li>
            </ol>
            <hr />
            <p>Next: <a href="https://github.com/munkycdev/bibliotaph/blob/main/searching.md">Searching</a></p>

            """.Replace("\r\n", "\n", StringComparison.Ordinal), html);
    }

    [Fact]
    public void The_guide_links_its_chapters_on_the_site_and_the_rest_on_GitHub()
    {
        const string markdown = """
            ## Changing a book's details

            See [Searching](searching.md), [Ask](reading.md#ask-this-book), [the guide](../../README.md#the-guide),
            [details](#changing-a-books-details), [features](../features.md) and [the site](https://bibliotaph.online/privacy).
            """;

        var html = SiteMarkdown.ToHtml(markdown, SiteGuide.Link);

        // The heading's id is GitHub's, so a link written for the repository finds it on the site too.
        Assert.Contains("""<h2 id="changing-a-books-details">""", html, StringComparison.Ordinal);
        Assert.Contains("""<a href="/guide/searching">Searching</a>""", html, StringComparison.Ordinal);
        Assert.Contains("""<a href="/guide/reading#ask-this-book">Ask</a>""", html, StringComparison.Ordinal);
        Assert.Contains("""<a href="/guide">the guide</a>""", html, StringComparison.Ordinal);
        Assert.Contains("""<a href="#changing-a-books-details">details</a>""", html, StringComparison.Ordinal);
        Assert.Contains("""<a href="https://github.com/munkycdev/bibliotaph/blob/main/docs/features.md">features</a>""", html, StringComparison.Ordinal);
        Assert.Contains("""<a href="https://bibliotaph.online/privacy">the site</a>""", html, StringComparison.Ordinal);
    }
}
