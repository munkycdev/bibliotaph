namespace Bibliotaph.Core.Tests;

public class DisplayTitleTests
{
    [Theory]
    [InlineData("Gazetteer of the Marches.pdf", "Gazetteer of the Marches")]
    [InlineData("Gazetteer_of_the_Marches_v1.2_digital.pdf", "Gazetteer of the Marches")]
    [InlineData("gazetteer_of_the_marches.pdf", "Gazetteer of the Marches")]
    [InlineData("the-sunken-keep-hi-res.pdf", "The Sunken Keep")]
    [InlineData("Half-Elf Heroes.pdf", "Half-Elf Heroes")]
    [InlineData("Cities of Ruin 20240115.pdf", "Cities of Ruin")]
    [InlineData("1001 Taverns.pdf", "1001 Taverns")]
    [InlineData("2024.pdf", "2024")]
    [InlineData("___.pdf", "___")]
    public void Makes_a_readable_title_from_a_file_name(string fileName, string expected) =>
        Assert.Equal(expected, DisplayTitle.FromFileName(fileName));

    [Theory]
    [InlineData("GazetteerOfTheMarches.pdf", "Gazetteer of the Marches")]
    [InlineData("gazetteerOfTheMarches.pdf", "Gazetteer of the Marches")]
    [InlineData("SunkenKeep_v2.pdf", "Sunken Keep")]
    [InlineData("SunkenKeepV2.pdf", "Sunken Keep")]
    [InlineData("DMGuideToTheMarches.pdf", "DM Guide to the Marches")]
    [InlineData("OSRBestiary.pdf", "OSR Bestiary")]
    [InlineData("ShadowOverAVillage.pdf", "Shadow Over a Village")]
    [InlineData("DnD5eHarborQuests.pdf", "DnD 5e Harbor Quests")]
    [InlineData("PF2eGoblinWarrens.pdf", "PF2e Goblin Warrens")]
    [InlineData("13thHourOfTheOwl.pdf", "13th Hour of the Owl")]
    [InlineData("Swords&SorcerySampler.pdf", "Swords & Sorcery Sampler")]
    [InlineData("Vault:TheReturn.pdf", "Vault: The Return")]
    [InlineData("McGuffinHunt.pdf", "McGuffin Hunt")]
    [InlineData("DarkForest_Gridded.jpg", "Dark Forest Gridded")]
    public void Splits_words_run_together(string fileName, string expected) =>
        Assert.Equal(expected, DisplayTitle.FromFileName(fileName));

    [Theory]
    [InlineData("Gazetteer of the Marches (1).pdf", "Gazetteer of the Marches")]
    [InlineData("Gazetteer of the Marches - Copy (2).pdf", "Gazetteer of the Marches")]
    [InlineData("Copy of Gazetteer of the Marches.pdf", "Gazetteer of the Marches")]
    [InlineData("[PDF] Gazetteer of the Marches [DTRPG].pdf", "Gazetteer of the Marches")]
    [InlineData("Gazetteer of the Marches (hi-res).pdf", "Gazetteer of the Marches")]
    [InlineData("Gazetteer of the Marches (Revised).pdf", "Gazetteer of the Marches (Revised)")]
    [InlineData("Gazetteer of the Marches - v1.3 - Print Friendly.pdf", "Gazetteer of the Marches")]
    [InlineData("Gazetteer_of_the_Marches_hi_res_no_bleed.pdf", "Gazetteer of the Marches")]
    [InlineData("XYZ12345_Gazetteer_of_the_Marches.pdf", "Gazetteer of the Marches")]
    [InlineData("Gazetteer_of_the_Marches_XYZ12345.pdf", "Gazetteer of the Marches")]
    [InlineData("98765_Gazetteer_of_the_Marches.pdf", "Gazetteer of the Marches")]
    [InlineData("Gazetteer of the Marches 2024-01-15.pdf", "Gazetteer of the Marches")]
    [InlineData("Gazetteer.of.the.Marches.v1.2.pdf", "Gazetteer of the Marches")]
    public void Drops_copy_markers_tags_and_codes(string fileName, string expected) =>
        Assert.Equal(expected, DisplayTitle.FromFileName(fileName));

    [Theory]
    [InlineData("Harbor Quests 2024.pdf", "Harbor Quests 2024")]
    [InlineData("Harbor Quests Part 2.pdf", "Harbor Quests Part 2")]
    [InlineData("Harbor_Quests_3.5.pdf", "Harbor Quests 3.5")]
    [InlineData("X1_Isle_of_Gulls.pdf", "X1 Isle of Gulls")]
    [InlineData("d&d_harbor_quests.pdf", "D&D Harbor Quests")]
    [InlineData("the_dm_guide_to_harbors.pdf", "The DM Guide to Harbors")]
    [InlineData("HARBOR_QUESTS_FOR_OSR_GAMES.pdf", "Harbor Quests for OSR Games")]
    [InlineData("Lord Of The Harbor.pdf", "Lord of the Harbor")]
    [InlineData("Vault of Ash - The Return.pdf", "Vault of Ash - The Return")]
    [InlineData("Something to Sail For.pdf", "Something to Sail For")]
    [InlineData("half-elf-harbors.pdf", "Half Elf Harbors")]
    [InlineData("3f9a8b7c6d5e4f30a1b2.pdf", "3f9a8b7c6d5e4f30a1b2")]
    public void Keeps_what_belongs_to_the_title(string fileName, string expected) =>
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
    [InlineData("Harbor.WebP", "webp")]
    [InlineData("Novel.EPUB", "epub")]
    [InlineData("Novel.mobi", "mobi")]
    [InlineData("Novel.prc", "mobi")]
    [InlineData("Novel.azw", "azw")]
    [InlineData("Novel.AZW3", "azw3")]
    [InlineData("Novel.kfx", "kfx")]
    [InlineData("notes.docx", null)]
    [InlineData("README", null)]
    public void Recognises_the_indexed_types(string name, string? expected) =>
        Assert.Equal(expected, SourceFormats.FromFileName(name));

    [Fact]
    public void An_EPUB_is_a_book_with_pages_and_never_a_ZIP_of_files()
    {
        Assert.True(SourceFormats.HasPages(SourceFormats.Epub));
        Assert.True(SourceFormats.HasPages(SourceFormats.Pdf));
        Assert.False(SourceFormats.HasPages(SourceFormats.Png));
        Assert.False(SourceFormats.IsArchive("Novel.epub"));
    }
}
