using System.Text;
using System.Text.Json;

namespace Bibliotaph.Classification;

/// <summary>A field of an answer still being written, with the values it has so far.</summary>
public sealed record PartialField(string Name, IReadOnlyList<string> Values);

/// <summary>
/// Reads an answer while the model is still writing it, for the AI reading page (Built-in AI plan, choice 12): each
/// field the answer has reached, in order, with the values whose text is complete. Nothing here is checked or stored;
/// the evidence check runs on the whole answer as before.
/// </summary>
public static class PartialAnswer
{
    public static IReadOnlyList<PartialField> Read(string answer)
    {
        var fields = new List<(string Name, List<string> Values)>();
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(answer), isFinalBlock: false, state: default);
        string? property = null;
        try
        {
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.PropertyName when reader.CurrentDepth == 1:
                        fields.Add((reader.GetString()!, []));
                        break;
                    case JsonTokenType.PropertyName:
                        property = reader.GetString();
                        break;
                    // Inside a field's array, each item's "value".
                    case JsonTokenType.String when reader.CurrentDepth == 3 && property == "value" && fields.Count > 0:
                        fields[^1].Values.Add(reader.GetString()!);
                        break;
                }
            }
        }
        catch (JsonException)
        {
            // Not JSON (yet, or at all): what was read so far stands.
        }
        return [.. fields.Select(f => new PartialField(f.Name, f.Values))];
    }
}
