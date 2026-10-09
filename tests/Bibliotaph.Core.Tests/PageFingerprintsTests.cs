namespace Bibliotaph.Core.Tests;

public class PageFingerprintsTests
{
    static readonly string[] Body =
    [
        "Chapter One\nThe owlbear waits beneath the old mill, guarding a clutch of eggs.",
        "The miller's daughter knows a back way in through the flooded cellar.",
        "Goblins raid the tavern at midnight and steal the ledger from the bar.",
        "A lich keeps the ledger now, in a crypt below the chapel on the hill.",
        "Appendix\nNew monsters: the mill wight, the cellar eel and the ledger mimic.",
    ];

    static string[] Watermarked(string buyer, int order) =>
        [.. Body.Select((page, i) => $"{page}\nPrepared exclusively for {buyer} (Order #{order})\n{i + 1}")];

    [Fact]
    public void Watermarked_copies_of_a_book_fingerprint_alike()
    {
        var first = PageFingerprints.Compute(Watermarked("Ana Ruiz", 1234));
        var second = PageFingerprints.Compute(Watermarked("Dale Example", 98765));

        Assert.Equal(first, second);
        Assert.All(first, Assert.NotNull);
        Assert.True(PageFingerprints.IsSameBook(first, second));
    }

    [Fact]
    public void Case_punctuation_and_spacing_do_not_matter()
    {
        var plain = PageFingerprints.Compute(Body);
        var messy = PageFingerprints.Compute([.. Body.Select(p => p.ToUpperInvariant().Replace(" ", "   ", StringComparison.Ordinal).Replace(",", " ;", StringComparison.Ordinal))]);

        Assert.Equal(plain, messy);
    }

    [Fact]
    public void A_changed_page_is_not_the_same_book()
    {
        var revised = Body.ToArray();
        revised[2] = "Goblins raid the tavern at dawn and steal the ledger from the bar.";

        var before = PageFingerprints.Compute(Body);
        var after = PageFingerprints.Compute(revised);

        Assert.NotEqual(before[2], after[2]);
        Assert.Equal(before[3], after[3]);
        Assert.False(PageFingerprints.IsSameBook(before, after));
    }

    [Fact]
    public void Pages_with_little_text_have_no_fingerprint()
    {
        var prints = PageFingerprints.Compute(["Map", "", null, Body[0]]);

        Assert.Equal([null, null, null], prints.Take(3));
        Assert.NotNull(prints[3]);
    }

    [Fact]
    public void Too_few_fingerprinted_pages_never_make_a_match()
    {
        // Two scans with no text layer, or two short handouts, are not evidence of anything.
        Assert.False(PageFingerprints.IsSameBook(PageFingerprints.Compute(["", "", ""]), PageFingerprints.Compute(["", "", ""])));
        string?[] handout = [Body[0], Body[1]];
        Assert.False(PageFingerprints.IsSameBook(PageFingerprints.Compute(handout), PageFingerprints.Compute(handout)));
        string?[] mostlyImages = [Body[0], Body[1], Body[2], "", "", "", "", ""];
        Assert.False(PageFingerprints.IsSameBook(PageFingerprints.Compute(mostlyImages), PageFingerprints.Compute(mostlyImages)));
    }

    [Fact]
    public void Different_page_counts_are_not_the_same_book()
    {
        var whole = PageFingerprints.Compute(Body);
        var extract = PageFingerprints.Compute(Body[..4]);

        Assert.False(PageFingerprints.IsSameBook(whole, extract));
    }

    [Fact]
    public void A_line_that_is_most_of_a_short_files_text_is_kept()
    {
        // Under four pages there is no telling a watermark from content, so nothing is dropped.
        string[] pages = [$"{Body[0]}\nShared line", $"{Body[1]}\nShared line"];
        string[] without = [Body[0], Body[1]];

        Assert.NotEqual(PageFingerprints.Compute(pages), PageFingerprints.Compute(without));
    }
}
