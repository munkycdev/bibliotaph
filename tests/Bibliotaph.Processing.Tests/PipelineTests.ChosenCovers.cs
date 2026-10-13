using Bibliotaph.Core;
using Bibliotaph.Index;

namespace Bibliotaph.Processing.Tests;

/// <summary>Cover images chosen for books owned elsewhere.</summary>
public sealed partial class PipelineTests
{
    [Fact]
    public async Task A_chosen_cover_shows_on_the_card_replaces_the_last_one_survives_remove_and_undo_and_can_be_taken_away()
    {
        await _service.StartAsync(Ct);
        var covers = new CoverCache(_paths);
        var chosen = new ChosenCovers(covers, new SizedCodec(), _entries, _projector);
        var search = new LibraryQueries(_index);
        var (printed, _) = await _elsewhere.AddAsync(new ElsewhereDraft("Tomb of the Serpent"), Ct);
        var picture = Path.Combine(_dir, "tomb.png");
        File.WriteAllBytes(picture, FakeCodec.Png(300, 400));
        async Task<string?> CoverAsync(EntryId entryId) =>
            (await search.ListAsync(new LibraryFilter(Kind: KindFilter.Elsewhere), ct: Ct)).Single(c => c.EntryId == entryId).Cover;

        Assert.Null(await chosen.ChooseAsync(printed!.Value, picture, Ct));
        var first = await CoverAsync(printed.Value);
        Assert.NotNull(first);
        Assert.True(File.Exists(Path.Combine(_paths.Covers, first)), "A chosen cover is kept with the user's data, not in the cache.");
        Assert.True(await chosen.HasChosenAsync(printed.Value, Ct));

        // Choosing another gives the card a new name to load, and the old one goes.
        File.WriteAllBytes(picture, FakeCodec.Png(600, 800));
        Assert.Null(await chosen.ChooseAsync(printed.Value, picture, Ct));
        var second = await CoverAsync(printed.Value);
        Assert.NotEqual(first, second);
        Assert.False(File.Exists(Path.Combine(_paths.Covers, first)));

        // Not a picture, or not a book owned elsewhere: a problem, and the cover stays.
        var text = Path.Combine(_dir, "notes.png");
        File.WriteAllText(text, "not a picture");
        Assert.NotNull(await chosen.ChooseAsync(printed.Value, text, Ct));
        Assert.Equal(second, await CoverAsync(printed.Value));

        // Removing the book and undoing it brings the cover back with it.
        var removed = await _elsewhere.RemoveAsync(printed.Value, Ct);
        var restored = await _elsewhere.UndoRemoveAsync(removed!, Ct);
        Assert.Equal(second, await CoverAsync(restored));

        await chosen.RemoveAsync(restored, Ct);
        Assert.Null(await CoverAsync(restored));
        Assert.False(await chosen.HasChosenAsync(restored, Ct));
    }

    /// <summary>A thumbnail that differs with the picture's size, so two pictures make two covers.</summary>
    sealed class SizedCodec : IImageCodec
    {
        readonly FakeCodec _codec = new();

        public byte[] EncodeJpeg(ReadOnlySpan<byte> bgra, int width, int height, int stride) => _codec.EncodeJpeg(bgra, width, height, stride);

        public (int Width, int Height)? ReadSize(Stream image) => _codec.ReadSize(image);

        public byte[]? Thumbnail(Stream image, int maxWidth) =>
            ReadSize(image) is { } size ? [0xFF, 0xD8, (byte)(size.Width / 100), (byte)(size.Height / 100)] : null;
    }
}
