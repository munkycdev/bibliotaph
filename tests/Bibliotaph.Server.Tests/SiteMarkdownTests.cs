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
}
