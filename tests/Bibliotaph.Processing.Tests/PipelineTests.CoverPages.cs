using Bibliotaph.Core;
using Bibliotaph.Index;
using Bibliotaph.Pdf.Host.Tests;

namespace Bibliotaph.Processing.Tests;

public abstract partial class PipelineTests
{
    /// <summary>"Use this page as the cover".</summary>
    public sealed class CoverPagesTests(SyntheticPdfs synthetic) : PipelineTests(synthetic)
    {
        [Fact]
        public async Task A_chosen_cover_page_is_drawn_at_once_kept_when_the_file_is_read_again_and_can_go_back_to_the_first_page()
        {
            Copy(_pdfs.KnownText, "Known Text.pdf");
            await _roots.AddAsync(_library, Ct);
            await _service.StartAsync(Ct);
            await SettleAsync();
            var search = new LibraryQueries(_index);
            var card = Assert.Single(await search.ListAsync(new LibraryFilter(), ct: Ct));
            var documentId = card.DocumentId;
            var first = card.Cover;
            Assert.NotNull(first);
            var covers = new CoverPages(_libraryStore, _entries, _stages);
            Assert.Equal(0, await covers.GetAsync(documentId, Ct));

            Assert.True(await covers.UseAsync(documentId, SyntheticPdfs.KnownPhrasePage, Ct));
            var chosen = Assert.Single(await search.ListAsync(new LibraryFilter(), ct: Ct)).Cover;
            Assert.NotEqual(first, chosen);
            Assert.True(File.Exists(Path.Combine(_paths.Cache, "covers", chosen!)));
            Assert.Equal(SyntheticPdfs.KnownPhrasePage, await covers.GetAsync(documentId, Ct));

            // Reading the file again draws the chosen page, not the first.
            await _service.RerunAsync(Stage.Covers, Ct);
            await SettleAsync();
            Assert.Equal(chosen, Assert.Single(await search.ListAsync(new LibraryFilter(), ct: Ct)).Cover);

            // The first page again: the cover it had before.
            Assert.True(await covers.UseAsync(documentId, 0, Ct));
            Assert.Equal(first, Assert.Single(await search.ListAsync(new LibraryFilter(), ct: Ct)).Cover);
            Assert.Equal(0, await covers.GetAsync(documentId, Ct));
        }
    }
}
