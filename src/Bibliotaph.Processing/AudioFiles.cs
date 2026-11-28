using System.Text.Json;
using Bibliotaph.Audio;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Index;

namespace Bibliotaph.Processing;

/// <summary>
/// Audio for the stages: what a track's tags say, in the fields a PDF's document information fills, so rule hints read
/// them the same way. The title tag is the embedded title and the artist the author; the album, genre, label and title
/// go with the keywords, which are matched against the vocabulary ("Rainy Tavern Loop" names a tavern); the comment is
/// the subject.
/// </summary>
static class AudioFiles
{
    public static DocRow Row(JobRecord job, DocumentSource source, string title, AudioDetails details) => new()
    {
        DocumentId = job.DocumentId,
        ContentHash = job.ContentHash,
        Format = source.Format,
        DisplayTitle = title,
        FolderHint = source.FolderHint,
        MetaTitle = details.Title,
        MetaAuthor = details.Credit,
        MetaSubject = details.Comment is { Length: > 1000 } comment ? comment[..1000] : details.Comment,
        MetaKeywords = string.Join(", ", new[] { details.Genre, details.Album, details.Publisher, details.Title }.OfType<string>().Distinct()) is { Length: > 0 } keywords
            ? keywords : null,
        DurationMs = (long)details.Duration.TotalMilliseconds,
    };

    /// <summary>What the details panel says about how the file plays.</summary>
    public static string Capabilities(AudioDetails details) => JsonSerializer.Serialize(new
    {
        codec = details.Codec,
        bitrate = details.BitrateKbps,
        sampleRate = details.SampleRate,
        album = details.Album,
        track = details.Track,
        disc = details.Disc,
        year = details.Year,
    });
}
