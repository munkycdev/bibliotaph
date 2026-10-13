using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bibliotaph.Index;

/// <summary>
/// The values picked in the Library's type-ahead filters, by field key ("setting", "publisher"), each field's in the
/// order they were picked. A book matches when, for every field with picks, it has any of that field's values. Two are
/// equal when they pick the same values in each field, in any order, so a saved search can tell when its filters
/// changed. Stored in JSON as an object of arrays: <c>{"setting": ["the-marches"]}</c>.
/// </summary>
[JsonConverter(typeof(PickedValuesConverter))]
public sealed class PickedValues : IEquatable<PickedValues>
{
    readonly SortedDictionary<string, IReadOnlyList<string>> _fields = [with(StringComparer.Ordinal)];

    /// <summary>Fields with no values are left out, and a value picked twice counts once.</summary>
    public PickedValues(IEnumerable<KeyValuePair<string, IReadOnlyList<string>>> fields)
    {
        foreach (var (field, values) in fields)
            if (values.Distinct(StringComparer.Ordinal).ToList() is { Count: > 0 } distinct) _fields[field] = distinct;
    }

    /// <summary>These picks, or null when no field has any: what a filter or saved search holds for "none picked".</summary>
    public static PickedValues? Of(IEnumerable<KeyValuePair<string, IReadOnlyList<string>>> fields) => new PickedValues(fields) is { IsEmpty: false } picked ? picked : null;

    public bool IsEmpty => _fields.Count == 0;

    /// <summary>The fields with picks, in key order.</summary>
    public IEnumerable<string> Fields => _fields.Keys;

    /// <summary>A field's picks, or none.</summary>
    public IReadOnlyList<string> this[string field] => _fields.GetValueOrDefault(field) ?? [];

    /// <summary>
    /// These picks without one field's, or null when nothing is left: a box counts its own values without them, so
    /// picking one setting still shows how many books the others have.
    /// </summary>
    public PickedValues? Without(string field) => Of(_fields.Where(f => f.Key != field));

    public bool Equals(PickedValues? other) =>
        other is not null && _fields.Keys.SequenceEqual(other._fields.Keys)
        && _fields.All(f => f.Value.Order(StringComparer.Ordinal).SequenceEqual(other._fields[f.Key].Order(StringComparer.Ordinal)));

    public override bool Equals(object? obj) => Equals(obj as PickedValues);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var (field, values) in _fields)
        {
            hash.Add(field);
            foreach (var value in values.Order(StringComparer.Ordinal)) hash.Add(value);
        }
        return hash.ToHashCode();
    }
}

/// <summary>Reads and writes <see cref="PickedValues"/> as an object of arrays, by field key.</summary>
sealed class PickedValuesConverter : JsonConverter<PickedValues>
{
    public override PickedValues Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new((JsonSerializer.Deserialize<Dictionary<string, List<string>?>>(ref reader, options) ?? [])
            .Select(f => KeyValuePair.Create(f.Key, (IReadOnlyList<string>)(f.Value ?? []))));

    public override void Write(Utf8JsonWriter writer, PickedValues value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value.Fields.ToDictionary(f => f, f => value[f], StringComparer.Ordinal), options);
}
