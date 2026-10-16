using System.Buffers.Binary;
using System.Text;

namespace Bibliotaph.Ebooks;

/// <summary>
/// A Mobipocket index (INDX records): a header record whose TAGX section says which tags an entry can carry, then
/// records of entries, each a name and its tags' values, and CNCX records holding the strings the values point at.
/// Kindle books keep their contents, and KF8 its skeletons and fragments, in these.
/// </summary>
sealed class MobiIndex
{
    /// <summary>Each entry's name and its tags, in the order they are stored.</summary>
    public List<(string Name, Dictionary<int, List<int>> Tags)> Entries { get; } = [];

    readonly Dictionary<int, string> _strings = [];

    /// <summary>A string of the CNCX records by its offset, or null.</summary>
    public string? String(int offset) => _strings.GetValueOrDefault(offset);

    public static MobiIndex Read(Func<int, byte[]?> record, int first, Encoding encoding)
    {
        var index = new MobiIndex();
        var header = record(first) ?? throw Damaged();
        var (count, _, strings, tagx) = Header(header);
        if (count > 10_000 || strings > 1_000) throw Damaged();
        for (var i = 0; i < strings; i++)
        {
            var cncx = record(first + count + 1 + i) ?? throw Damaged();
            var pos = 0;
            while (pos < cncx.Length)
            {
                var (length, used) = Number(cncx.AsSpan(pos));
                if (used == 0) break;
                if (length > 0 && pos + used + length <= cncx.Length) index._strings[pos + i * 0x10000] = encoding.GetString(cncx, pos + used, length);
                pos += used + length;
            }
        }

        if (tagx < 0 || tagx + 12 > header.Length || !header.AsSpan(tagx, 4).SequenceEqual("TAGX"u8)) throw Damaged();
        var tagxLength = (int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(tagx + 4));
        var controlBytes = (int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(tagx + 8));
        if (tagx + tagxLength > header.Length || controlBytes is < 1 or > 8) throw Damaged();
        var tags = new List<(int Tag, int Values, int Mask, bool End)>();
        for (var p = tagx + 12; p + 4 <= tagx + tagxLength; p += 4) tags.Add((header[p], header[p + 1], header[p + 2], header[p + 3] == 1));

        for (var r = first + 1; r <= first + count; r++)
        {
            var data = record(r) ?? throw Damaged();
            var (entries, start, _, _) = Header(data);
            if (start < 0 || start + 4 + entries * 2 > data.Length) throw Damaged();
            var positions = new int[entries + 1];
            for (var j = 0; j < entries; j++) positions[j] = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(start + 4 + j * 2));
            positions[entries] = start;
            for (var j = 0; j < entries; j++)
            {
                if (positions[j] >= positions[j + 1] || positions[j + 1] > data.Length) throw Damaged();
                var entry = data.AsSpan(positions[j], positions[j + 1] - positions[j]);
                var nameLength = entry[0];
                if (1 + nameLength > entry.Length) throw Damaged();
                var name = Encoding.Latin1.GetString(entry.Slice(1, nameLength));
                index.Entries.Add((name, TagValues(entry[(1 + nameLength)..], controlBytes, tags)));
            }
        }
        return index;
    }

    static (int Count, int Start, int Strings, int Tagx) Header(byte[] data)
    {
        if (data.Length < 184 || !data.AsSpan(0, 4).SequenceEqual("INDX"u8)) throw Damaged();
        int Field(int i) => (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4 + i * 4));
        // len, nul1, type, gen, start, count, code, lng, total, ordt, ligt, nligt, ncncx, 27 unknown, ocnt, oentries, ordt1, ordt2, tagx
        return (Field(5), Field(4), Field(12), Field(44));
    }

    static Dictionary<int, List<int>> TagValues(ReadOnlySpan<byte> entry, int controlCount, List<(int Tag, int Values, int Mask, bool End)> tags)
    {
        var result = new Dictionary<int, List<int>>();
        if (entry.Length < controlCount) return result;
        var control = entry[..controlCount].ToArray();
        var data = entry[controlCount..];
        var c = 0;
        var present = new List<(int Tag, int? Count, int? Bytes, int Values)>();
        foreach (var tag in tags)
        {
            if (tag.End)
            {
                c++;
                continue;
            }
            if (c >= control.Length) break;
            var value = control[c] & tag.Mask;
            if (value == 0) continue;
            if (value == tag.Mask)
            {
                if (int.PopCount(tag.Mask) > 1)
                {
                    var (bytes, used) = Number(data);
                    data = data[used..];
                    present.Add((tag.Tag, null, bytes, tag.Values));
                }
                else present.Add((tag.Tag, 1, null, tag.Values));
            }
            else
            {
                var mask = tag.Mask;
                while ((mask & 1) == 0)
                {
                    mask >>= 1;
                    value >>= 1;
                }
                present.Add((tag.Tag, value, null, tag.Values));
            }
        }
        foreach (var (tag, count, bytes, values) in present)
        {
            var list = new List<int>();
            if (count is { } n)
            {
                for (var i = 0; i < n * values && data.Length > 0; i++)
                {
                    var (v, used) = Number(data);
                    data = data[used..];
                    list.Add(v);
                }
            }
            else
            {
                var consumed = 0;
                while (consumed < bytes && data.Length > 0)
                {
                    var (v, used) = Number(data);
                    data = data[used..];
                    consumed += used;
                    list.Add(v);
                }
            }
            result[tag] = list;
        }
        return result;
    }

    /// <summary>A variable-width number: seven bits a byte, the last byte marked by its high bit.</summary>
    internal static (int Value, int Used) Number(ReadOnlySpan<byte> data)
    {
        var value = 0;
        for (var i = 0; i < Math.Min(4, data.Length); i++)
        {
            value = (value << 7) | (data[i] & 0x7F);
            if ((data[i] & 0x80) != 0) return (value, i + 1);
        }
        return (value, Math.Min(4, data.Length));
    }

    static EbookException Damaged() => new("This Kindle book's index is damaged.");
}
