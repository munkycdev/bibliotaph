using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

namespace Bibliotaph.Core;

/// <summary>
/// SHA-256 over a whole file, as 64 lowercase hex characters. A document is its content hash;
/// a path is only where that content was last seen.
/// </summary>
public readonly record struct ContentHash
{
    public const int HexLength = SHA256.HashSizeInBytes * 2;

    readonly string? _hex;

    ContentHash(string hex) => _hex = hex;

    public string Hex => _hex ?? throw new InvalidOperationException("Default ContentHash has no value.");

    public static ContentHash FromBytes(ReadOnlySpan<byte> sha256)
    {
        if (sha256.Length != SHA256.HashSizeInBytes)
            throw new ArgumentException($"A SHA-256 hash is {SHA256.HashSizeInBytes} bytes, not {sha256.Length}.", nameof(sha256));
        return new ContentHash(Convert.ToHexStringLower(sha256));
    }

    public static ContentHash Parse(string hex) =>
        TryParse(hex, out var hash) ? hash : throw new FormatException($"'{hex}' is not a SHA-256 hex string.");

    public static bool TryParse([NotNullWhen(true)] string? hex, out ContentHash hash)
    {
        hash = default;
        if (hex is null || hex.Length != HexLength || !hex.All(char.IsAsciiHexDigit)) return false;
        hash = new ContentHash(hex.ToLowerInvariant());
        return true;
    }

    public override string ToString() => _hex ?? "";
}
