using System.Buffers.Binary;

namespace Bibliotaph.Ebooks;

/// <summary>
/// The two ways a Mobipocket book's text records are compressed: PalmDOC's byte-oriented LZ77, and the Huffman code
/// with phrase dictionaries (HUFF and CDIC records) that Mobipocket Creator used. Both stop at a size limit, so a
/// crafted record can't expand without end.
/// </summary>
static class MobiCompression
{
    /// <summary>PalmDOC: literals, runs of up to 8 raw bytes, a space before a character, and back references of 3 to 10 bytes.</summary>
    public static byte[] PalmDoc(ReadOnlySpan<byte> data, int limit)
    {
        var output = new List<byte>(Math.Min(limit, data.Length * 2));
        var i = 0;
        while (i < data.Length)
        {
            if (output.Count > limit) throw TooLarge();
            var c = data[i++];
            if (c is >= 1 and <= 8)
            {
                var end = Math.Min(data.Length, i + c);
                for (; i < end; i++) output.Add(data[i]);
            }
            else if (c < 0x80) output.Add(c);
            else if (c >= 0xC0)
            {
                output.Add((byte)' ');
                output.Add((byte)(c ^ 0x80));
            }
            else
            {
                if (i >= data.Length) break;
                var pair = (c << 8) | data[i++];
                var distance = (pair >> 3) & 0x7FF;
                var length = (pair & 7) + 3;
                if (distance == 0 || distance > output.Count) throw new EbookException("This Kindle book's text is damaged.");
                var from = output.Count - distance;
                for (var n = 0; n < length; n++) output.Add(output[from + n]);
            }
        }
        if (output.Count > limit) throw TooLarge();
        return [.. output];
    }

    static EbookException TooLarge() => new("This Kindle book's text is larger than Bibliotaph reads.");

    /// <summary>
    /// Mobipocket's Huffman decoder. Codes are read 32 bits at a time from the top; the first byte looks up a code length
    /// (and, for short codes, the end of the code), longer codes are found by the per-length minimums, and each code
    /// names a dictionary phrase, which may itself be compressed and is unpacked once on first use.
    /// </summary>
    public sealed class Huffman
    {
        const int MaxNesting = 32;
        readonly (int Length, bool Terminal, uint Max)[] _first = new (int, bool, uint)[256];
        readonly ulong[] _minimum = new ulong[33];
        readonly ulong[] _maximum = new ulong[33];
        readonly List<(byte[] Phrase, bool Unpacked)> _phrases = [];
        readonly int _limit;

        public Huffman(byte[] huff, IEnumerable<byte[]> cdics, int limit)
        {
            _limit = limit;
            if (huff.Length < 24 || !huff.AsSpan(0, 8).SequenceEqual("HUFF\0\0\0\u0018"u8)) throw Damaged();
            var table1 = (int)BinaryPrimitives.ReadUInt32BigEndian(huff.AsSpan(8));
            var table2 = (int)BinaryPrimitives.ReadUInt32BigEndian(huff.AsSpan(12));
            if (table1 < 0 || table1 + 1024 > huff.Length || table2 < 0 || table2 + 256 > huff.Length) throw Damaged();
            for (var i = 0; i < 256; i++)
            {
                var v = BinaryPrimitives.ReadUInt32BigEndian(huff.AsSpan(table1 + i * 4));
                var length = (int)(v & 0x1F);
                if (length == 0) throw Damaged();
                _first[i] = (length, (v & 0x80) != 0, (uint)((((ulong)(v >> 8) + 1) << (32 - length)) - 1));
            }
            for (var length = 1; length <= 32; length++)
            {
                var min = BinaryPrimitives.ReadUInt32BigEndian(huff.AsSpan(table2 + (length - 1) * 8));
                var max = BinaryPrimitives.ReadUInt32BigEndian(huff.AsSpan(table2 + (length - 1) * 8 + 4));
                _minimum[length] = (ulong)min << (32 - length);
                _maximum[length] = (((ulong)max + 1) << (32 - length)) - 1;
            }
            foreach (var cdic in cdics) AddDictionary(cdic);
        }

        void AddDictionary(byte[] cdic)
        {
            if (cdic.Length < 16 || !cdic.AsSpan(0, 8).SequenceEqual("CDIC\0\0\0\u0010"u8)) throw Damaged();
            var phrases = BinaryPrimitives.ReadUInt32BigEndian(cdic.AsSpan(8));
            var bits = (int)BinaryPrimitives.ReadUInt32BigEndian(cdic.AsSpan(12));
            if (bits > 31) throw Damaged();
            var count = (int)Math.Min(1L << bits, (long)phrases - _phrases.Count);
            for (var i = 0; i < count; i++)
            {
                if (16 + i * 2 + 2 > cdic.Length) throw Damaged();
                var offset = BinaryPrimitives.ReadUInt16BigEndian(cdic.AsSpan(16 + i * 2));
                if (16 + offset + 2 > cdic.Length) throw Damaged();
                var header = BinaryPrimitives.ReadUInt16BigEndian(cdic.AsSpan(16 + offset));
                var length = header & 0x7FFF;
                if (18 + offset + length > cdic.Length) throw Damaged();
                _phrases.Add((cdic.AsSpan(18 + offset, length).ToArray(), (header & 0x8000) != 0));
            }
        }

        public byte[] Unpack(ReadOnlySpan<byte> data)
        {
            var output = new List<byte>(data.Length * 3);
            Unpack(data, output, 0);
            return [.. output];
        }

        void Unpack(ReadOnlySpan<byte> data, List<byte> output, int nesting)
        {
            if (nesting > MaxNesting) throw Damaged();
            var padded = new byte[data.Length + 8];
            data.CopyTo(padded);
            long bitsLeft = data.Length * 8L;
            var position = 0;
            var window = BinaryPrimitives.ReadUInt64BigEndian(padded);
            var n = 32;
            while (true)
            {
                if (n <= 0)
                {
                    position += 4;
                    if (position + 8 > padded.Length) break;
                    window = BinaryPrimitives.ReadUInt64BigEndian(padded.AsSpan(position));
                    n += 32;
                }
                var code = (window >> n) & 0xFFFFFFFF;
                var (length, terminal, max) = _first[code >> 24];
                ulong maxCode = max;
                if (!terminal)
                {
                    while (length < 32 && code < _minimum[length]) length++;
                    maxCode = _maximum[length];
                }
                n -= length;
                bitsLeft -= length;
                if (bitsLeft < 0) break;
                var index = (int)((maxCode - code) >> (32 - length));
                if (index < 0 || index >= _phrases.Count) throw Damaged();
                var (phrase, unpacked) = _phrases[index];
                if (!unpacked)
                {
                    var expanded = new List<byte>(phrase.Length * 2);
                    Unpack(phrase, expanded, nesting + 1);
                    phrase = [.. expanded];
                    _phrases[index] = (phrase, true);
                }
                output.AddRange(phrase);
                if (output.Count > _limit) throw TooLarge();
            }
        }

        static EbookException Damaged() => new("This Kindle book's text is damaged.");
    }
}
