namespace Bibliotaph.Core.Tests;

public class DisplayTitleTests
{
    [Theory]
    [InlineData("Gazetteer of the Marches.pdf", "Gazetteer of the Marches")]
    [InlineData("Gazetteer_of_the_Marches_v1.2_digital.pdf", "Gazetteer of the Marches")]
    [InlineData("gazetteer_of_the_marches.pdf", "Gazetteer Of The Marches")]
    [InlineData("the-sunken-keep-hi-res.pdf", "The Sunken Keep")]
    [InlineData("Half-Elf Heroes.pdf", "Half-Elf Heroes")]
    [InlineData("Cities of Ruin 20240115.pdf", "Cities of Ruin")]
    [InlineData("1001 Taverns.pdf", "1001 Taverns")]
    [InlineData("2024.pdf", "2024")]
    [InlineData("___.pdf", "___")]
    public void Makes_a_readable_title_from_a_file_name(string fileName, string expected) =>
        Assert.Equal(expected, DisplayTitle.FromFileName(fileName));
}

public class TextQualityTests
{
    const string Prose = "The owlbear waits beneath the old mill until midnight, then hunts along the river.";

    [Fact]
    public void Clean_prose_scores_high_and_is_not_sent_to_ocr()
    {
        var score = TextQuality.Score(Prose, unmappedChars: 0);

        Assert.True(score > 0.9, $"Score {score}");
        Assert.False(TextQuality.NeedsOcr(score));
    }

    [Theory]
    [InlineData("")]
    [InlineData("12")]
    [InlineData("Page 4")]
    public void A_page_with_almost_no_text_needs_ocr(string text) =>
        Assert.True(TextQuality.NeedsOcr(TextQuality.Score(text, 0)));

    [Fact]
    public void Text_the_engine_could_not_map_to_unicode_needs_ocr() =>
        Assert.True(TextQuality.NeedsOcr(TextQuality.Score(Prose, unmappedChars: Prose.Length)));

    [Fact]
    public void A_junk_text_layer_of_symbols_needs_ocr() =>
        Assert.True(TextQuality.NeedsOcr(TextQuality.Score("#$%& ()*+ ,-./ 0123 :;<= >?@[ \\]^_ `{|}~ !\"#$ %&'(", 0)));

    [Fact]
    public void A_stat_block_full_of_numbers_still_counts_as_text() =>
        Assert.False(TextQuality.NeedsOcr(TextQuality.Score("Armor Class 15 (natural armor) Hit Points 59 (7d10 + 21) Speed 40 ft. STR 20 (+5) DEX 12 (+1)", 0)));
}

public class SourceFormatsTests
{
    [Theory]
    [InlineData("Book.PDF", "pdf")]
    [InlineData("map.jpeg", "jpg")]
    [InlineData("map.JPG", "jpg")]
    [InlineData("handout.png", "png")]
    [InlineData("notes.docx", null)]
    [InlineData("README", null)]
    public void Recognises_the_indexed_types(string name, string? expected) =>
        Assert.Equal(expected, SourceFormats.FromFileName(name));

    [Fact]
    public void Ocr_runs_in_its_own_lane() =>
        Assert.Equal([Lane.Index, Lane.Index, Lane.Index, Lane.Ocr], new[] { Stage.Probe, Stage.Text, Stage.Covers, Stage.Ocr }.Select(Pipeline.LaneOf));
}
