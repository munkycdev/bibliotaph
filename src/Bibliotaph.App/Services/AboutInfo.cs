using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Bibliotaph.App.Services;

/// <summary>
/// What the About popup shows and "Copy details" copies for a bug report. It names builds, never paths or books, so
/// nothing private ends up pasted into a public issue.
/// </summary>
public sealed record AboutInfo(string Version, string? Commit, string Runtime, string Windows, string? Pdfium)
{
    public const string SourceUrl = "https://github.com/munkycdev/bibliotaph";
    public const string IssuesUrl = SourceUrl + "/issues";
    public const string Copyright = "Copyright © 2026 munkycdev";

    /// <summary>"0.4.0 (4578ab9)", or just the version for a build made outside git.</summary>
    public string VersionText => Commit is null ? Version : $"{Version} ({Commit})";

    public string Details => string.Join(Environment.NewLine,
        $"Bibliotaph {VersionText}",
        Runtime,
        Windows,
        $"PDFium {Pdfium ?? "not found"}");

    public static AboutInfo ForThisApp()
    {
        var informational = typeof(AboutInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        var (version, commit) = ParseVersion(informational);
        return new AboutInfo(version, commit, RuntimeInformation.FrameworkDescription,
            $"Windows {Environment.OSVersion.Version.ToString(3)}", PdfiumVersion());
    }

    /// <summary>
    /// Splits the SDK's informational version, "0.4.0+4578ab9c…" (the commit is appended at build time), into the
    /// version and the commit's first seven characters.
    /// </summary>
    public static (string Version, string? Commit) ParseVersion(string informational)
    {
        var plus = informational.IndexOf('+', StringComparison.Ordinal);
        if (plus < 0) return (informational, null);
        var commit = informational[(plus + 1)..];
        return (informational[..plus], commit.Length == 0 ? null : commit[..Math.Min(7, commit.Length)]);
    }

    // The worker binds PDFium through PDFiumCore, whose version is the PDFium build it wraps (156.0.8076).
    static string? PdfiumVersion()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "pdfworker", "PDFiumCore.dll");
            return File.Exists(path) ? AssemblyName.GetAssemblyName(path).Version?.ToString(3) : null;
        }
        catch (Exception ex) when (ex is IOException or BadImageFormatException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
