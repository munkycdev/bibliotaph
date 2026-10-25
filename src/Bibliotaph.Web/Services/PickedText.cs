using System.Text.Json;

namespace Bibliotaph.Web.Services;

/// <summary>
/// The type-ahead filters' picks as a <see cref="Api.Contracts.LibraryQuery.Picked"/> holds them: JSON, an object of
/// arrays by field key, each field's values in the order they were picked. Fields are written in key order, so the
/// same picks make the same address.
/// </summary>
public static class PickedText
{
    public static IReadOnlyList<string> Values(string? json, string field) => Read(json).GetValueOrDefault(field) ?? [];

    public static string? Add(string? json, string field, string value)
    {
        var fields = Read(json);
        var values = fields.GetValueOrDefault(field) ?? [];
        if (!values.Contains(value, StringComparer.Ordinal)) fields[field] = [.. values, value];
        return Write(fields);
    }

    public static string? Remove(string? json, string field, string value)
    {
        var fields = Read(json);
        if (fields.TryGetValue(field, out var values)) fields[field] = [.. values.Where(v => v != value)];
        return Write(fields);
    }

    static SortedDictionary<string, List<string>> Read(string? json)
    {
        if (string.IsNullOrEmpty(json)) return new(StringComparer.Ordinal);
        try
        {
            var read = JsonSerializer.Deserialize<Dictionary<string, List<string>?>>(json) ?? [];
            return new(read.ToDictionary(f => f.Key, f => f.Value ?? []), StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new(StringComparer.Ordinal);
        }
    }

    /// <summary>The picks as JSON, or null when no field has any.</summary>
    static string? Write(SortedDictionary<string, List<string>> fields)
    {
        var picked = fields.Where(f => f.Value.Count > 0).ToDictionary(f => f.Key, f => f.Value);
        return picked.Count == 0 ? null : JsonSerializer.Serialize(picked);
    }
}
