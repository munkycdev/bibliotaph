using System.Text;
using System.Text.Json;

namespace Bibliotaph.Classification;

/// <summary>A field of an answer still being written, with the values it has so far.</summary>
public sealed record PartialField(string Name, IReadOnlyList<string> Values);

/// <summary>
/// Reads an answer while the model is still writing it, for the AI reading page: each field the answer has reached, in
/// order, with the values whose text is complete. Nothing here is checked or stored; the evidence check runs on the
/// whole answer as before.
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

    /// <summary>
    /// A JSON answer that stopped part way, cut back to the last value it finished and closed, so it reads as JSON: each
    /// item it completed stays, and the one it was writing goes. Null when it finished nothing. An answer that did finish
    /// is returned as it is.
    /// </summary>
    public static string? Close(string answer)
    {
        var text = answer.TrimStart();
        // A model that fences its JSON and runs out never writes the closing fence.
        if (text.StartsWith("```", StringComparison.Ordinal)) text = text.IndexOf('\n', StringComparison.Ordinal) is var line and >= 0 ? text[(line + 1)..] : "";
        var bytes = Encoding.UTF8.GetBytes(text);
        var reader = new Utf8JsonReader(bytes, isFinalBlock: false, state: default);
        var open = new Stack<char>();
        var kept = 0L;
        var closers = "";
        try
        {
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                        open.Push('}');
                        break;
                    case JsonTokenType.StartArray:
                        open.Push(']');
                        break;
                    case JsonTokenType.EndObject or JsonTokenType.EndArray:
                        open.Pop();
                        if (open.Count == 0) return Encoding.UTF8.GetString(bytes, 0, (int)reader.BytesConsumed);
                        Keep(ref reader);
                        break;
                    // A string, number, true, false or null finishes an item when it is one, not when it is half an object.
                    case not JsonTokenType.PropertyName when open.Count > 0 && open.Peek() == ']':
                        Keep(ref reader);
                        break;
                }
            }
        }
        catch (JsonException)
        {
            // Where the answer stopped making sense: what was finished before it stands.
        }
        return kept == 0 ? null : Encoding.UTF8.GetString(bytes, 0, (int)kept) + closers;

        void Keep(ref Utf8JsonReader reader)
        {
            kept = reader.BytesConsumed;
            closers = new string([.. open]);
        }
    }
}
