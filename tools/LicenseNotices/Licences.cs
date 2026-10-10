namespace LicenseNotices;

/// <summary>
/// The licences a package that ships with Bibliotaph may have: GPL-3.0-compatible ones only (architecture,
/// "Licences"). Each maps to its standard text in texts\ (from the SPDX licence list), written out for a package that
/// names its licence by SPDX id and carries no licence file of its own.
/// </summary>
static class Licences
{
    /// <summary>The GPL's text is Bibliotaph's own LICENSE, at the repository's root.</summary>
    public const string GplText = "LICENSE";

    /// <summary>
    /// SPDX ids, which compare without case, with the deprecated forms NuGet still accepts. Not here on purpose: MS-PL
    /// and MS-RL, which the FSF lists as free but incompatible with the GPL (their copyleft requires code under them
    /// to be distributed under the same licence and no other); AGPL code (MuPDF, iText, Ghostscript); and any licence
    /// that limits use by revenue. GPL-2.0-only is incompatible with GPL-3.0; GPL-2.0-or-later code would be fine, but
    /// nothing ships under it, so it is left off until something does and someone has looked.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Allowed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["MIT"] = "MIT.txt",
        ["Apache-2.0"] = "Apache-2.0.txt",
        ["BSD-2-Clause"] = "BSD-2-Clause.txt",
        ["BSD-3-Clause"] = "BSD-3-Clause.txt",
        ["ISC"] = "ISC.txt",
        ["Zlib"] = "Zlib.txt",
        ["LGPL-2.1-only"] = "LGPL-2.1.txt",
        ["LGPL-2.1-or-later"] = "LGPL-2.1.txt",
        ["LGPL-2.1"] = "LGPL-2.1.txt",
        ["LGPL-2.1+"] = "LGPL-2.1.txt",
        // The SPDX text of the LGPL 3 carries the GPL 3 it adds permissions to.
        ["LGPL-3.0-only"] = "LGPL-3.0.txt",
        ["LGPL-3.0-or-later"] = "LGPL-3.0.txt",
        ["LGPL-3.0"] = "LGPL-3.0.txt",
        ["LGPL-3.0+"] = "LGPL-3.0.txt",
        ["GPL-3.0-only"] = GplText,
        ["GPL-3.0-or-later"] = GplText,
        ["GPL-3.0"] = GplText,
        ["GPL-3.0+"] = GplText,
        // SQLite's public-domain dedication: public-domain code can be combined with code under any licence.
        ["blessing"] = "blessing.txt",
    };

    /// <summary>
    /// Packages that ship their licence as a file rather than an SPDX id. Someone read the file and named its licence;
    /// the file must still say <see cref="KnownFile.Says"/>, so a later version that changes its terms fails until it
    /// is read again.
    /// </summary>
    public static readonly KnownFile[] Files =
    [
        new("SQLite", "blessing", "SQLite is Public Domain"),
    ];

    /// <summary>
    /// Parts of the platform that ship beside the app under the platform's own terms. The GPL (section 1) lets a
    /// covered work use its "System Libraries" whatever their licence: parts of the operating system or the compiler's
    /// toolchain that serve only to let the program use them.
    /// </summary>
    public static readonly SystemLibrary[] System =
    [
        new("Microsoft.Windows.SDK.NET.Ref", "Microsoft Windows SDK licence terms, https://aka.ms/WinSDKLicenseURL",
            "The .NET SDK's projection of Windows' own APIs (Microsoft.Windows.SDK.NET.dll, and WinRT.Runtime.dll from " +
            "C#/WinRT, which is MIT), which the PDF worker uses for Windows' text recognition. It comes with the .NET " +
            "SDK and serves only to call Windows, so it is a System Library in the GPL's terms."),
    ];

    /// <summary>
    /// The licences to write out for an SPDX expression: for OR, the first choice that is allowed; for AND, all of
    /// them. Null, with the reason, when no choice is allowed or a part isn't known.
    /// </summary>
    public static (IReadOnlyList<string>? Ids, string? Problem) Choose(string expression)
    {
        try
        {
            var parser = new Parser(expression);
            var chosen = parser.Expression();
            parser.End();
            return chosen.Ids is null ? (null, chosen.Problem) : (chosen.Ids.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), null);
        }
        catch (FormatException ex)
        {
            return (null, $"\"{expression}\" isn't an SPDX expression this tool reads: {ex.Message}");
        }
    }

    /// <summary>expression := and ("OR" and)*; and := atom ("AND" atom)*; atom := "(" expression ")" | id ["WITH" exception].</summary>
    sealed class Parser(string text)
    {
        readonly List<string> _tokens = [.. text.Replace("(", " ( ", StringComparison.Ordinal).Replace(")", " ) ", StringComparison.Ordinal)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)];
        int _at;

        public Choice Expression()
        {
            var choices = new List<Choice> { And() };
            while (Take("OR")) choices.Add(And());
            return choices.FirstOrDefault(c => c.Ids is not null)
                ?? new Choice(null, string.Join("; ", choices.Select(c => c.Problem)));
        }

        Choice And()
        {
            var parts = new List<Choice> { Atom() };
            while (Take("AND")) parts.Add(Atom());
            var refused = parts.Where(p => p.Ids is null).ToList();
            return refused.Count > 0
                ? new Choice(null, string.Join("; ", refused.Select(p => p.Problem)))
                : new Choice([.. parts.SelectMany(p => p.Ids!)], null);
        }

        Choice Atom()
        {
            if (Take("("))
            {
                var inner = Expression();
                if (!Take(")")) throw new FormatException("a bracket isn't closed");
                return inner;
            }
            var id = Next() ?? throw new FormatException("a licence id is missing");
            if (id is "(" or ")" or "AND" or "OR" or "WITH") throw new FormatException($"\"{id}\" is out of place");
            // An exception only adds permissions, but none is on the list until someone has read it.
            if (Take("WITH")) return new Choice(null, $"{id} WITH {Next()} has a licence exception that isn't on the list");
            return Allowed.ContainsKey(id) ? new Choice([id], null) : new Choice(null, $"{id} isn't on the list of GPL-3.0-compatible licences");
        }

        public void End()
        {
            if (_at < _tokens.Count) throw new FormatException($"\"{_tokens[_at]}\" is out of place");
        }

        bool Take(string token)
        {
            if (_at >= _tokens.Count || !string.Equals(_tokens[_at], token, StringComparison.OrdinalIgnoreCase)) return false;
            _at++;
            return true;
        }

        string? Next() => _at < _tokens.Count ? _tokens[_at++] : null;
    }

    sealed record Choice(IReadOnlyList<string>? Ids, string? Problem);
}

/// <summary>A package whose licence file was read and found to be <see cref="Licence"/>, an SPDX id on the list.</summary>
sealed record KnownFile(string Id, string Licence, string Says);

/// <summary>A part of the platform that ships beside the app under <see cref="Licence"/>, and why that is allowed.</summary>
sealed record SystemLibrary(string Id, string Licence, string Why);
