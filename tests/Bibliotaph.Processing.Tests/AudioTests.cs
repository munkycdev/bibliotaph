using System.Text;
using Bibliotaph.Audio;
using Bibliotaph.Core;

namespace Bibliotaph.Processing.Tests;

/// <summary>Builds small audio files: an MP3 of silent frames under an ID3v2.3 tag, and a WAV of silence.</summary>
static class FakeAudio
{
    /// <summary>One MPEG-1 Layer III frame at 128 kbps and 44.1 kHz plays 1152 samples, about 26 ms.</summary>
    public static byte[] Mp3(string? title = null, string? artist = null, string? album = null, string? track = null, string? genre = null,
        byte[]? artwork = null, int frames = 400)
    {
        var tag = new List<byte>();
        if (title is not null) tag.AddRange(Text("TIT2", title));
        if (artist is not null) tag.AddRange(Text("TPE1", artist));
        if (album is not null) tag.AddRange(Text("TALB", album));
        if (track is not null) tag.AddRange(Text("TRCK", track));
        if (genre is not null) tag.AddRange(Text("TCON", genre));
        // Front cover (picture type 3), its MIME type as Latin-1, an empty description.
        if (artwork is not null) tag.AddRange(Frame("APIC", [0, .. Encoding.ASCII.GetBytes("image/png"), 0, 3, 0, .. artwork]));
        var size = tag.Count;
        var bytes = new List<byte>(Encoding.ASCII.GetBytes("ID3")) { 3, 0, 0 };
        bytes.AddRange([(byte)((size >> 21) & 0x7F), (byte)((size >> 14) & 0x7F), (byte)((size >> 7) & 0x7F), (byte)(size & 0x7F)]);
        bytes.AddRange(tag);
        for (var i = 0; i < frames; i++)
        {
            var frame = new byte[417];
            (frame[0], frame[1], frame[2], frame[3]) = (0xFF, 0xFB, 0x90, 0x64);
            bytes.AddRange(frame);
        }
        return [.. bytes];
    }

    /// <summary>Seconds of 8 kHz mono 16-bit silence, with no tags.</summary>
    public static byte[] Wav(int seconds)
    {
        const int rate = 8000;
        var data = rate * 2 * seconds;
        var bytes = new List<byte>();
        void Ascii(string s) => bytes.AddRange(Encoding.ASCII.GetBytes(s));
        void Int(int v) => bytes.AddRange(BitConverter.GetBytes(v));
        void Short(short v) => bytes.AddRange(BitConverter.GetBytes(v));
        Ascii("RIFF"); Int(36 + data); Ascii("WAVE");
        Ascii("fmt "); Int(16); Short(1); Short(1); Int(rate); Int(rate * 2); Short(2); Short(16);
        Ascii("data"); Int(data);
        bytes.AddRange(new byte[data]);
        return [.. bytes];
    }

    static byte[] Text(string id, string value) => Frame(id, [3, .. Encoding.UTF8.GetBytes(value)]);

    static byte[] Frame(string id, byte[] body)
    {
        var n = body.Length;
        return [.. Encoding.ASCII.GetBytes(id), (byte)(n >> 24), (byte)(n >> 16), (byte)(n >> 8), (byte)n, 0, 0, .. body];
    }
}

public sealed class AudioFileTests
{
    [Fact]
    public void Reads_an_MP3s_tags_and_how_long_it_plays_from_its_headers()
    {
        var mp3 = FakeAudio.Mp3("Rainy Tavern", "A Composer", "Tavern Sounds", "3/12", "Ambient", frames: 1000);
        var details = AudioFile.Read(new MemoryStream(mp3), SourceFormats.Mp3);

        Assert.NotNull(details);
        Assert.Equal(("Rainy Tavern", "A Composer", "Tavern Sounds", 3, "Ambient"), (details.Title, details.Credit, details.Album, details.Track, details.Genre));
        // 1,000 frames of 1,152 samples at 44.1 kHz.
        Assert.InRange(details.Duration.TotalSeconds, 25, 27.5);
        Assert.Equal((128, 44100), (details.BitrateKbps, details.SampleRate));
    }

    [Fact]
    public void A_file_with_no_tags_has_no_title_rather_than_one_made_from_its_name()
    {
        var details = AudioFile.Read(new MemoryStream(FakeAudio.Wav(3)), SourceFormats.Wav);

        Assert.NotNull(details);
        Assert.Null(details.Title);
        Assert.Null(details.Credit);
        Assert.InRange(details.Duration.TotalSeconds, 2.9, 3.1);
    }

    [Fact]
    public void Reads_the_artwork_an_MP3_carries()
    {
        var art = FakeCodecPng();
        Assert.Equal(art, AudioFile.ReadArtwork(new MemoryStream(FakeAudio.Mp3("Theme", artwork: art)), SourceFormats.Mp3));
        Assert.Null(AudioFile.ReadArtwork(new MemoryStream(FakeAudio.Mp3("Theme")), SourceFormats.Mp3));
    }

    [Fact]
    public void A_file_that_isnt_audio_or_a_format_that_isnt_reads_as_nothing()
    {
        Assert.Null(AudioFile.Read(new MemoryStream(new byte[5000]), SourceFormats.Mp3));
        Assert.Null(AudioFile.Read(new MemoryStream(Encoding.ASCII.GetBytes("not audio at all")), SourceFormats.Wav));
        Assert.Null(AudioFile.Read(new MemoryStream(FakeAudio.Mp3("Theme")), SourceFormats.Pdf));
    }

    [Theory]
    [InlineData(SourceFormats.Mp3, "audio/mpeg")]
    [InlineData(SourceFormats.M4a, "audio/mp4")]
    [InlineData(SourceFormats.Flac, "audio/flac")]
    [InlineData(SourceFormats.Wav, "audio/wav")]
    [InlineData(SourceFormats.Ogg, "audio/ogg")]
    [InlineData(SourceFormats.Pdf, null)]
    public void Every_audio_format_has_the_media_type_players_take(string format, string? expected) => Assert.Equal(expected, AudioFile.MediaType(format));

    [Fact]
    public void Every_audio_format_has_a_media_type()
    {
        Assert.All(SourceFormats.Audio, f => Assert.NotNull(AudioFile.MediaType(f)));
    }

    // A PNG signature and IHDR, as the pipeline's fake codec reads.
    static byte[] FakeCodecPng()
    {
        var png = new byte[33];
        new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' }.CopyTo(png, 0);
        png[19] = 200;
        png[23] = 200;
        return png;
    }
}
