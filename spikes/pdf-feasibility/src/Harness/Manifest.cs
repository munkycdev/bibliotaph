using System.Text.Json;
using Bibliotaph.Spike.Contracts;

namespace Bibliotaph.Spike.Harness;

sealed record Manifest
{
    /// <summary>Folder that relative paths resolve against, unless an entry says otherwise.</summary>
    public string Root { get; init; } = "";
    public List<ManifestEntry> Files { get; init; } = [];

    public static Manifest Load(string path)
    {
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path), new JsonSerializerOptions(Framing.Json) { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Manifest is empty.");
        var manifestDir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        return manifest with
        {
            Files = manifest.Files.Select(f => f with
            {
                FullPath = Path.GetFullPath(Path.IsPathRooted(f.Path)
                    ? f.Path
                    : Path.Combine(f.RelativeTo == "manifest" ? manifestDir : manifest.Root, f.Path)),
            }).ToList(),
        };
    }
}

sealed record ManifestEntry
{
    public string Path { get; init; } = "";
    public string Category { get; init; } = "";

    /// <summary>"manifest" resolves the path against the manifest's folder (generated fixtures).</summary>
    public string? RelativeTo { get; init; }

    /// <summary>"prompt" asks at run time; "fixture:&lt;value&gt;" is only for generated test files. Never put a real password here.</summary>
    public string? Password { get; init; }

    /// <summary>A word the harness searches for, and the PDF page (1-based) it is expected on.</summary>
    public string? FindWord { get; init; }
    public int? FindExpectedPage { get; init; }

    public string FullPath { get; init; } = "";
}
