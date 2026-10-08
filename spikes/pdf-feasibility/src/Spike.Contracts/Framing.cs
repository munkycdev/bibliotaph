using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bibliotaph.Spike.Contracts;

/// <summary>Length-prefixed JSON messages: a 4-byte little-endian length, then UTF-8 JSON.</summary>
public static class Framing
{
    const int MaxMessageBytes = 64 * 1024 * 1024;

    public static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static void Write<T>(Stream stream, T message)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        Span<byte> header = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
        stream.Write(header);
        stream.Write(body);
        stream.Flush();
    }

    /// <summary>Returns null when the other side closed the stream cleanly.</summary>
    public static T? Read<T>(Stream stream) where T : class
    {
        var header = new byte[4];
        if (!ReadExactly(stream, header)) return null;
        var length = ReadLength(header);
        var body = new byte[length];
        if (!ReadExactly(stream, body)) throw new EndOfStreamException("Stream closed mid-message.");
        return JsonSerializer.Deserialize<T>(body, Json);
    }

    public static async Task WriteAsync<T>(Stream stream, T message, CancellationToken ct)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(body, ct);
        await stream.FlushAsync(ct);
    }

    public static async Task<T?> ReadAsync<T>(Stream stream, CancellationToken ct) where T : class
    {
        var header = new byte[4];
        if (!await ReadExactlyAsync(stream, header, ct)) return null;
        var length = ReadLength(header);
        var body = new byte[length];
        if (!await ReadExactlyAsync(stream, body, ct)) throw new EndOfStreamException("Stream closed mid-message.");
        return JsonSerializer.Deserialize<T>(body, Json);
    }

    static int ReadLength(byte[] header)
    {
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 0 || length > MaxMessageBytes) throw new InvalidDataException($"Bad message length {length}.");
        return length;
    }

    static bool ReadExactly(Stream stream, byte[] buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = stream.Read(buffer, read, buffer.Length - read);
            if (n == 0) return read == 0 ? false : throw new EndOfStreamException("Stream closed mid-message.");
            read += n;
        }
        return true;
    }

    static async Task<bool> ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), ct);
            if (n == 0) return read == 0 ? false : throw new EndOfStreamException("Stream closed mid-message.");
            read += n;
        }
        return true;
    }
}
