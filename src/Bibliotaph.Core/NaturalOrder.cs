namespace Bibliotaph.Core;

/// <summary>
/// File names in the order File Explorer lists them: runs of digits compare as numbers ("Goblin 2" before
/// "Goblin 10"), everything else without regard to case.
/// </summary>
public sealed class NaturalOrder : IComparer<string>
{
    public static NaturalOrder Instance { get; } = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;
        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
            {
                var (a, nextI) = Number(x, i);
                var (b, nextJ) = Number(y, j);
                var byValue = a.TrimStart('0').Length.CompareTo(b.TrimStart('0').Length);
                if (byValue == 0) byValue = string.CompareOrdinal(a.TrimStart('0'), b.TrimStart('0'));
                if (byValue != 0) return byValue;
                (i, j) = (nextI, nextJ);
                continue;
            }
            var byChar = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
            if (byChar != 0) return byChar;
            i++;
            j++;
        }
        var byLength = (x.Length - i).CompareTo(y.Length - j);
        return byLength != 0 ? byLength : string.CompareOrdinal(x, y);
    }

    static (string Digits, int Next) Number(string s, int start)
    {
        var end = start;
        while (end < s.Length && char.IsAsciiDigit(s[end])) end++;
        return (s[start..end], end);
    }
}
