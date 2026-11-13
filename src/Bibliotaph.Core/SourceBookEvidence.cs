using System.Text.Json;

namespace Bibliotaph.Core;

/// <summary>What told Bibliotaph which book a companion goes with, strongest first.</summary>
public enum SourceBookSignal
{
    /// <summary>Its pages are pages of the book: a handout printed from it.</summary>
    SharedPages,
    /// <summary>It came in the same ZIP as the book, the only book in it.</summary>
    SameZip,
    /// <summary>It sits beside the book, the only book in its folder, or in a folder of extras under it.</summary>
    SameFolder,
    /// <summary>Its name carries the book's title, initials or product code.</summary>
    Name,
    /// <summary>The model read its pages and quoted where they name the book.</summary>
    Ai,
}

/// <summary>
/// Why a companion is linked to a book, kept with the link as JSON: the signal, what it saw (a folder, a name, the
/// quote), and a score that ranks the books one companion is proposed for.
/// </summary>
public sealed record SourceBookEvidence(SourceBookSignal Signal, string? Detail = null, double Score = 0)
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Signals strong enough to link without asking, when they point at exactly one book.</summary>
    public bool IsStrong => Signal is SourceBookSignal.SharedPages or SourceBookSignal.SameZip or SourceBookSignal.SameFolder;

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static SourceBookEvidence? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<SourceBookEvidence>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>How it was found, in words: "It sits beside the book in “Maps”".</summary>
    public string Describe() => Signal switch
    {
        SourceBookSignal.SharedPages => Detail is { Length: > 0 } pages ? $"Its pages are in the book ({pages})" : "Its pages are in the book",
        SourceBookSignal.SameZip => Detail is { Length: > 0 } zip ? $"It came in the same ZIP, “{zip}”" : "It came in the same ZIP",
        SourceBookSignal.SameFolder => Detail is { Length: > 0 } folder ? $"It sits beside the book in “{folder}”" : "It sits beside the book",
        SourceBookSignal.Name => Detail is { Length: > 0 } name ? $"Its name carries “{name}”" : "Its name carries the book's",
        SourceBookSignal.Ai => Detail is { Length: > 0 } quote ? $"The AI read “{quote}”" : "The AI matched it",
        _ => "",
    };
}
