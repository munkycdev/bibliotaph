using System.Text;
using Bibliotaph.Catalog;

namespace Bibliotaph.Processing.Tests;

/// <summary>Builds the start of a WebM file: EBML header, a segment with Info and Tracks, then a Cluster.</summary>
static class FakeWebm
{
    public static byte[] Bytes(int width, int height, string docType = "webm", bool unknownSegmentSize = false, bool audioFirst = false)
    {
        var info = Element([0x15, 0x49, 0xA9, 0x66], Element([0x2A, 0xD7, 0xB1], Uint(1_000_000)));
        var video = Element([0xAE],
            Element([0xD7], Uint(audioFirst ? 2 : 1)),
            Element([0x83], Uint(1)),
            Element([0x86], Encoding.ASCII.GetBytes("V_VP9")),
            Element([0xE0], Element([0xB0], Uint(width)), Element([0xBA], Uint(height))));
        var audio = Element([0xAE], Element([0xD7], Uint(1)), Element([0x83], Uint(2)), Element([0x86], Encoding.ASCII.GetBytes("A_OPUS")));
        var tracks = Element([0x16, 0x54, 0xAE, 0x6B], audioFirst ? [.. audio, .. video] : video);
        var cluster = Element([0x1F, 0x43, 0xB6, 0x75], Element([0xE7], Uint(0)));
        byte[] body = [.. info, .. tracks, .. cluster];
        byte[] segment = unknownSegmentSize
            ? [0x18, 0x53, 0x80, 0x67, 0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, .. body]
            : Element([0x18, 0x53, 0x80, 0x67], body);
        var header = Element([0x1A, 0x45, 0xDF, 0xA3], Element([0x42, 0x86], Uint(1)), Element([0x42, 0x82], Encoding.ASCII.GetBytes(docType)));
        return [.. header, .. segment];
    }

    static byte[] Uint(long value)
    {
        var bytes = BitConverter.GetBytes(value).Reverse().SkipWhile(b => b == 0).ToArray();
        return bytes.Length == 0 ? [0] : bytes;
    }

    static byte[] Element(byte[] id, params byte[][] children)
    {
        byte[] body = [.. children.SelectMany(c => c)];
        // Lengths as 8-byte vints, as some muxers write them, so the reader is tried on more than one length size.
        var size = new byte[8];
        size[0] = 0x01;
        for (var i = 0; i < 7; i++) size[7 - i] = (byte)((long)body.Length >> (8 * i));
        return [.. id, .. size, .. body];
    }
}

public sealed class WebmHeaderTests
{
    [Fact]
    public void Reads_the_size_of_the_video_track()
    {
        Assert.Equal((1920, 1080), WebmHeader.ReadSize(FakeWebm.Bytes(1920, 1080)));
        Assert.Equal((4096, 2304), WebmHeader.ReadSize(new MemoryStream(FakeWebm.Bytes(4096, 2304))));
    }

    [Fact]
    public void Skips_an_audio_track_and_reads_a_segment_of_unknown_length()
    {
        Assert.Equal((1280, 720), WebmHeader.ReadSize(FakeWebm.Bytes(1280, 720, audioFirst: true)));
        Assert.Equal((1280, 720), WebmHeader.ReadSize(FakeWebm.Bytes(1280, 720, unknownSegmentSize: true)));
        Assert.Equal((800, 600), WebmHeader.ReadSize(FakeWebm.Bytes(800, 600, docType: "matroska")));
    }

    [Fact]
    public void Anything_else_has_no_size()
    {
        Assert.Null(WebmHeader.ReadSize(FakeWebm.Bytes(1920, 1080, docType: "quicktime")));
        Assert.Null(WebmHeader.ReadSize(FakeWebm.Bytes(1920, 1080).AsSpan(0, 40)));
        Assert.Null(WebmHeader.ReadSize("<html>not a video</html>"u8));
        Assert.Null(WebmHeader.ReadSize([]));
        Assert.Null(WebmHeader.ReadSize(new byte[64]));
    }
}

public sealed class VideoStillsTests
{
    static StillBeside Still(long id, string name) => new(id, Path.Combine("Maps", "Harbor", name));

    [Fact]
    public void The_still_named_most_like_the_video_is_its_cover()
    {
        IReadOnlyList<StillBeside> stills = [Still(1, "GL_Harbor_Day.jpg"), Still(2, "GL_Harbor_Night.jpg"), Still(3, "G_Harbor_Night.jpg")];
        Assert.Equal(2, VideoStills.Pick("Maps/Harbor/GL_Harbor_Night_Animated.webm", stills));
        Assert.Equal(3, VideoStills.Pick("Maps/Harbor/G_Harbor_Night.webm", stills));
        Assert.Equal(1, VideoStills.Pick("Maps/Harbor/GL_HarborDay.webm", stills));
    }

    [Fact]
    public void Without_a_shared_word_the_first_still_in_natural_order_is_taken_and_without_stills_none()
    {
        IReadOnlyList<StillBeside> stills = [Still(1, "Map 10.png"), Still(2, "Map 2.png")];
        Assert.Equal(2, VideoStills.Pick("Maps/Harbor/Storm.webm", stills));
        Assert.Null(VideoStills.Pick("Maps/Harbor/Storm.webm", []));
    }

    [Fact]
    public void Animated_in_a_videos_name_doesnt_count_against_a_still()
    {
        IReadOnlyList<StillBeside> stills = [Still(1, "AffluentAmphitheatre_Fire_Animated_Preview.jpg"), Still(2, "Affluent Amphitheatre Fire.jpg")];
        Assert.Equal(2, VideoStills.Pick("Maps/Harbor/AffluentAmphitheatre_Fire_Animated.webm", stills));
    }
}

public sealed class MediaRangeTests
{
    [Theory]
    [InlineData("bytes=0-", 0, 1000)]
    [InlineData("bytes=0-99", 0, 100)]
    [InlineData("bytes=900-5000", 900, 100)]
    [InlineData("bytes=-200", 800, 200)]
    [InlineData("bytes=-5000", 0, 1000)]
    [InlineData("BYTES=10-19", 10, 10)]
    public void Answers_one_range(string header, long start, long length)
    {
        var range = MediaRange.Parse(header, 1000)!;
        Assert.Equal((start, length, 1000L), (range.Start, range.Length, range.Total));
        Assert.Equal($"bytes {start}-{start + length - 1}/1000", range.ContentRange);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("items=0-10")]
    [InlineData("bytes=0-10,20-30")]
    [InlineData("bytes=abc-")]
    [InlineData("bytes=50-10")]
    [InlineData("bytes=-0")]
    public void Sends_the_whole_file_for_anything_else(string? header) => Assert.Null(MediaRange.Parse(header, 1000));

    [Fact]
    public void A_range_past_the_end_has_no_bytes()
    {
        Assert.Equal(0, MediaRange.Parse("bytes=1000-", 1000)!.Length);
    }

    [Fact]
    public void Opens_just_its_part_of_the_file()
    {
        var file = new MemoryStream([.. Enumerable.Range(0, 100).Select(i => (byte)i)]);
        using var part = MediaRange.Parse("bytes=10-14", 100)!.Open(file);
        var read = new byte[50];
        var count = part.ReadAtLeast(read, 50, throwOnEndOfStream: false);
        Assert.Equal([10, 11, 12, 13, 14], read.AsSpan(0, count).ToArray());
    }
}
