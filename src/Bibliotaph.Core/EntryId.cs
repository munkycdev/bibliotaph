using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bibliotaph.Core;

/// <summary>
/// A library card's id: entry.id in catalog.db. What the user decides (metadata, review, collections) is about an
/// entry; pages, text and files belong to a document, one content version behind it. A migrated entry starts with its
/// document's id, so the two would pass for each other until a second copy joins an entry; a type of its own keeps
/// them apart.
/// </summary>
[JsonConverter(typeof(EntryIdJsonConverter))]
public readonly record struct EntryId(long Value)
{
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Writes an <see cref="EntryId"/> as its number, so lists of them serialize as SQLite's json_each reads them.</summary>
public sealed class EntryIdJsonConverter : JsonConverter<EntryId>
{
    public override EntryId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => new(reader.GetInt64());

    public override void Write(Utf8JsonWriter writer, EntryId value, JsonSerializerOptions options) => writer.WriteNumberValue(value.Value);
}
