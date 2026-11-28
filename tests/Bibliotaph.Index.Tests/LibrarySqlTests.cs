using Bibliotaph.Core;

namespace Bibliotaph.Index.Tests;

public sealed class LibrarySqlTests
{
    [Fact]
    public void The_SQL_list_of_audio_formats_names_every_audio_format()
    {
        // The list is fixed text in the card queries, so a format added to SourceFormats must be added there too.
        var listed = LibrarySql.AudioFormats.Split(',').Select(f => f.Trim().Trim('\'')).Order();
        Assert.Equal(SourceFormats.Audio.Order(), listed);
    }
}
