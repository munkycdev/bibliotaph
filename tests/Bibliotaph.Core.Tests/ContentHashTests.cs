using System.Security.Cryptography;
using System.Text;

namespace Bibliotaph.Core.Tests;

public class ContentHashTests
{
    [Fact]
    public void FromBytes_formats_lowercase_hex()
    {
        var hash = ContentHash.FromBytes(SHA256.HashData(Encoding.UTF8.GetBytes("bibliotaph")));

        Assert.Equal(64, hash.Hex.Length);
        Assert.Equal(hash.Hex.ToLowerInvariant(), hash.Hex);
    }

    [Fact]
    public void Parse_accepts_uppercase_and_normalises_it()
    {
        var hex = new string('A', 64);

        Assert.Equal(new string('a', 64), ContentHash.Parse(hex).Hex);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public void TryParse_rejects_anything_but_64_hex_characters(string? hex) =>
        Assert.False(ContentHash.TryParse(hex, out _));

    [Fact]
    public void Equal_hashes_are_equal_regardless_of_input_case() =>
        Assert.Equal(ContentHash.Parse(new string('F', 64)), ContentHash.Parse(new string('f', 64)));

    [Fact]
    public void FromBytes_rejects_the_wrong_length() =>
        Assert.Throws<ArgumentException>(() => ContentHash.FromBytes(new byte[16]));
}
