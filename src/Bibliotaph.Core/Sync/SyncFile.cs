using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Bibliotaph.Core.Sync;

/// <summary>
/// Field values in the library sync file: compact JSON, written and read with the same options everywhere, so a value
/// read back from another PC's file is the same string as the one this PC would write for it.
/// </summary>
public static class SyncJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public static string Value<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Read<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    /// <summary>A value read from a file, written back out the way <see cref="Value{T}"/> writes it.</summary>
    public static string Canonical(JsonElement element) => JsonSerializer.Serialize(element, Options);
}

/// <summary>One PC's sync file as read: who wrote it, when, and its records.</summary>
public sealed record SyncFileContent(string Machine, Guid MachineId, string? App, DateTime WrittenUtc, IReadOnlyList<SyncRecord> Records);

/// <summary>
/// The library sync file, one per device, so no two devices ever write the same file. It holds the device's user work
/// keyed by content hash and sync id, each field with when it changed, and the tombstones of what was deleted. Never
/// file locations, online-only marks, window places or caches: those belong to one device.
/// </summary>
public static class SyncFile
{
    public const string Format = "bibliotaph-sync";

    /// <summary>The file's own version, which goes up when its shape changes. A newer one is refused rather than half read.</summary>
    public const int FormatVersion = 1;

    const string About = "Bibliotaph's library sync file: this PC's user work (metadata decisions, hearts, notes, collections, binders, saved searches, pins), "
        + "by content hash for books and by sync id for what the user made, each field with when it last changed. Each PC writes only its own file "
        + "and reads the others', newest change per field winning. The databases themselves are never put here.";

    public static async Task WriteAsync(Stream stream, string machine, Guid machineId, string app, DateTime writtenUtc, IEnumerable<SyncRecord> records,
        CancellationToken ct = default)
    {
        await using var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        json.WriteStartObject();
        json.WriteString("format", Format);
        json.WriteNumber("formatVersion", FormatVersion);
        json.WriteString("machine", machine);
        json.WriteString("machineId", machineId);
        json.WriteString("app", app);
        json.WriteString("writtenUtc", Utc(writtenUtc));
        json.WriteString("about", About);
        json.WriteStartArray("records");
        foreach (var record in records.OrderBy(r => r.Key, StringComparer.Ordinal))
        {
            json.WriteStartObject();
            json.WriteString("key", record.Key);
            if (record.DeletedUtc is { } deleted) json.WriteString("deletedUtc", Utc(deleted));
            if (record.Aliases.Count > 0)
            {
                json.WriteStartArray("aliases");
                foreach (var alias in record.Aliases) json.WriteStringValue(alias);
                json.WriteEndArray();
            }
            json.WriteStartObject("fields");
            foreach (var (name, field) in record.Fields.OrderBy(f => f.Key, StringComparer.Ordinal))
            {
                json.WriteStartObject(name);
                json.WritePropertyName("value");
                if (field.Value is { } value) json.WriteRawValue(value, skipInputValidation: false);
                else json.WriteNullValue();
                json.WriteString("changedUtc", Utc(field.ChangedUtc));
                json.WriteEndObject();
            }
            json.WriteEndObject();
            json.WriteEndObject();
            if (json.BytesPending > 64 * 1024) await json.FlushAsync(ct);
        }
        json.WriteEndArray();
        json.WriteEndObject();
        await json.FlushAsync(ct);
    }

    /// <summary>Reads a PC's file. Throws <see cref="InvalidDataException"/> for one that isn't a sync file, or is from a newer Bibliotaph.</summary>
    public static async Task<SyncFileContent> ReadAsync(Stream stream, CancellationToken ct = default)
    {
        JsonDocument document;
        try
        {
            document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("It isn't a Bibliotaph sync file: " + ex.Message, ex);
        }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.String || format.GetString() != Format)
                throw new InvalidDataException("It isn't a Bibliotaph sync file.");
            if (!root.TryGetProperty("formatVersion", out var version) || !version.TryGetInt32(out var number) || number > FormatVersion)
                throw new InvalidDataException("It was written by a newer Bibliotaph. Update Bibliotaph on this PC to merge it.");
            try
            {
                var records = new List<SyncRecord>();
                foreach (var item in root.GetProperty("records").EnumerateArray())
                {
                    var fields = new Dictionary<string, SyncField>(StringComparer.Ordinal);
                    foreach (var field in item.GetProperty("fields").EnumerateObject())
                    {
                        var value = field.Value.GetProperty("value");
                        fields[field.Name] = new SyncField(value.ValueKind == JsonValueKind.Null ? null : SyncJson.Canonical(value),
                            ParseUtc(field.Value.GetProperty("changedUtc").GetString()));
                    }
                    var deleted = item.TryGetProperty("deletedUtc", out var d) && d.ValueKind == JsonValueKind.String ? ParseUtc(d.GetString()) : (DateTime?)null;
                    var aliases = item.TryGetProperty("aliases", out var a) && a.ValueKind == JsonValueKind.Array
                        ? a.EnumerateArray().Select(x => x.GetString()).OfType<string>().ToList() : [];
                    records.Add(new SyncRecord(item.GetProperty("key").GetString() ?? throw new InvalidDataException("A record has no key."), fields, deleted)
                    {
                        Aliases = aliases,
                    });
                }
                return new SyncFileContent(root.GetProperty("machine").GetString() ?? "", root.GetProperty("machineId").GetGuid(),
                    root.TryGetProperty("app", out var app) ? app.GetString() : null, ParseUtc(root.GetProperty("writtenUtc").GetString()), records);
            }
            catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException)
            {
                throw new InvalidDataException("The sync file is damaged: " + ex.Message, ex);
            }
        }
    }

    static string Utc(DateTime time) =>
        DateTime.SpecifyKind(time, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    static DateTime ParseUtc(string? text) =>
        DateTime.Parse(text ?? throw new FormatException("A time is missing."), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
}
