using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using LicenseNotices;

// Writes licenses\packages\<id>.txt in an app folder (a build's output, or the published app) for every NuGet package
// its .deps.json files say ships: the app's, and the PDF worker's in pdfworker\. Each file has the package's licence
// file and notices from the NuGet cache or, when it only names an SPDX id, the standard text kept in texts\. PDFium,
// which the pdfium-binaries package carries without its notices, gets pdfium.txt from pdfium\. Fails, writing nothing,
// when a package's licence isn't known or isn't GPL-3.0-compatible (Licences.cs), so CI stops before it ships.

if (args is not [var appArgument])
{
    Console.Error.WriteLine("Usage: LicenseNotices <the app's folder, holding Bibliotaph.deps.json>");
    return 2;
}
var app = Path.GetFullPath(appArgument);
var repo = FindRepoRoot();
var here = Path.Combine(repo, "tools", "LicenseNotices");
var output = Path.Combine(app, "licenses", "packages");
var roots = PackageRoots().ToList();

var depsFiles = Directory.Exists(app) ? Directory.GetFiles(app, "*.deps.json", SearchOption.AllDirectories) : [];
if (depsFiles.Length == 0)
{
    Console.Error.WriteLine($"No .deps.json in {app}: build or publish the app first.");
    return 1;
}

// Id, case as the first .deps.json spells it, and every version that ships.
var shipped = new SortedDictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
foreach (var depsFile in depsFiles)
{
    using var deps = JsonDocument.Parse(File.ReadAllText(depsFile));
    foreach (var library in deps.RootElement.GetProperty("libraries").EnumerateObject())
    {
        var type = library.Value.GetProperty("type").GetString();
        if (type is not ("package" or "runtimepack")) continue;
        var slash = library.Name.LastIndexOf('/');
        var id = library.Name[..slash];
        // A self-contained app's runtime (and the Windows SDK projection) are runtime packs, NuGet packages too.
        if (type == "runtimepack") id = id["runtimepack.".Length..];
        if (!shipped.TryGetValue(id, out var versions)) shipped[id] = versions = [with(StringComparer.OrdinalIgnoreCase)];
        versions.Add(library.Name[(slash + 1)..]);
    }
}

var problems = new List<string>();
var files = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
foreach (var (id, versions) in shipped)
{
    var sections = new List<string>();
    foreach (var version in versions)
    {
        if (FindPackage(id, version) is not { } folder)
        {
            problems.Add($"{id} {version}: not found in {string.Join(" or ", roots)}. Restore first.");
            continue;
        }
        if (Describe(id, version, folder) is { } text) sections.Add(text);
    }
    if (sections.Count == versions.Count) files[id + ".txt"] = string.Join(Rule(), sections);
}

// The PDFium build inside bblanchon.PDFium.Win32, whose notices (PDFium's and its bundled libraries') come with the
// pdfium-binaries release but not the package. Kept here per build, so a new build fails until its notices are added.
if (shipped.TryGetValue("bblanchon.PDFium.Win32", out var pdfium))
{
    foreach (var version in pdfium)
    {
        var build = version.Split('.')[^1];
        var notices = Path.Combine(here, "pdfium", $"chromium-{build}");
        if (!Directory.Exists(notices))
        {
            problems.Add($"PDFium build {build} (bblanchon.PDFium.Win32 {version}) has no notices in {notices}. Put that " +
                $"pdfium-binaries release's LICENSE and licenses folder there (https://github.com/bblanchon/pdfium-binaries/releases/tag/chromium%2F{build}).");
            continue;
        }
        List<string> parts =
        [
            $"PDFium, build chromium/{build}, from pdfium-binaries (bblanchon.PDFium.Win32 {version})",
            Section("LICENSE", File.ReadAllText(Path.Combine(notices, "LICENSE"))),
        ];
        foreach (var file in Directory.GetFiles(Path.Combine(notices, "licenses")).Order(StringComparer.OrdinalIgnoreCase))
            parts.Add(Section($"licenses/{Path.GetFileName(file)}", File.ReadAllText(file)));
        files["pdfium.txt"] = string.Join(Environment.NewLine + Environment.NewLine, parts);
    }
}

if (problems.Count > 0)
{
    Console.Error.WriteLine($"Licences: {problems.Count} {(problems.Count == 1 ? "problem" : "problems")}, nothing written.");
    foreach (var problem in problems) Console.Error.WriteLine("  " + problem);
    return 1;
}

// Rewritten each time, so a package that no longer ships leaves no licence behind.
if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
Directory.CreateDirectory(output);
foreach (var (name, text) in files)
    File.WriteAllText(Path.Combine(output, name), text.ReplaceLineEndings("\r\n").TrimEnd() + "\r\n", new UTF8Encoding(false));
Console.WriteLine($"Licences: wrote {files.Count} files for {shipped.Count} packages to {output}");
return 0;

// One package version's licence: what it is, who holds the copyright, and the texts, or null after noting a problem.
string? Describe(string id, string version, string folder)
{
    var nuspecPath = Directory.GetFiles(folder, "*.nuspec").FirstOrDefault();
    if (nuspecPath is null)
    {
        problems.Add($"{id} {version}: no .nuspec in {folder}.");
        return null;
    }
    var metadata = XDocument.Load(nuspecPath).Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "metadata");
    string? Meta(string name) => metadata?.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim() is { Length: > 0 } value ? value : null;
    var license = metadata?.Elements().FirstOrDefault(e => e.Name.LocalName == "license");
    var licenseType = license?.Attribute("type")?.Value;

    // Licence and notice files at the package's root: the real copyright lines, and any NOTICE Apache-2.0 asks to pass on.
    var rootFiles = Directory.GetFiles(folder).Select(Path.GetFileName).OfType<string>().Order(StringComparer.OrdinalIgnoreCase).ToList();
    var licenceFiles = rootFiles.Where(IsLicenceFile).ToList();
    var noticeFiles = rootFiles.Where(IsNoticeFile).ToList();

    string licence;
    var texts = new List<(string Title, string Text)>();
    if (Licences.System.FirstOrDefault(s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) is { } system)
    {
        licence = $"{system.Licence}. {system.Why}";
    }
    else if (licenseType == "expression")
    {
        licence = license!.Value.Trim();
        var (ids, problem) = Licences.Choose(licence);
        if (ids is null)
        {
            problems.Add($"{id} {version}: {problem}.");
            return null;
        }
        if (licenceFiles.Count == 0)
            texts.AddRange(ids.Select(spdx => ($"{spdx} (standard text)", StandardText(spdx, Meta("copyright") ?? Meta("authors")))));
    }
    else if (Licences.Files.FirstOrDefault(k => k.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) is { } known)
    {
        var file = licenseType == "file" ? license!.Value.Trim() : null;
        var text = file is not null && File.Exists(Path.Combine(folder, file)) ? File.ReadAllText(Path.Combine(folder, file)) : null;
        if (text is null || !text.Contains(known.Says, StringComparison.Ordinal))
        {
            problems.Add($"{id} {version}: its licence file no longer says \"{known.Says}\". Read it again and update Licences.Files.");
            return null;
        }
        licence = known.Licence;
        if (!licenceFiles.Contains(file!, StringComparer.OrdinalIgnoreCase)) texts.Add((file!, text));
    }
    else
    {
        var named = licenseType == "file" ? $"its licence is the file {license!.Value.Trim()}" : $"its licence is only a link, {Meta("licenseUrl") ?? "or none at all"}";
        problems.Add($"{id} {version}: {named}. Read its terms; if they are GPL-3.0-compatible, add the package to Licences.cs.");
        return null;
    }

    texts.AddRange(licenceFiles.Concat(noticeFiles).Select(name => (name, File.ReadAllText(Path.Combine(folder, name)))));
    var header = new StringBuilder();
    header.AppendLine($"{Meta("id") ?? id} {version}");
    header.AppendLine($"Licence: {licence}");
    if (Meta("copyright") is { } copyright) header.AppendLine($"Copyright: {copyright}");
    if (Meta("authors") is { } authors) header.AppendLine($"Authors: {authors}");
    if (Meta("projectUrl") is { } project) header.AppendLine($"Project: {project}");
    return string.Join(Environment.NewLine + Environment.NewLine, [header.ToString().TrimEnd(), .. texts.Select(t => Section(t.Title, t.Text))]);
}

// The standard text, with the package's copyright holder in place of a template's "Copyright (c) <year> <holder>".
string StandardText(string spdx, string? holder)
{
    var file = Licences.Allowed[spdx];
    var text = File.ReadAllText(file == Licences.GplText ? Path.Combine(repo, file) : Path.Combine(here, "texts", file));
    if (holder is null) return text;
    var line = holder.StartsWith("Copyright", StringComparison.OrdinalIgnoreCase) || holder.StartsWith('©') || holder.StartsWith("(c)", StringComparison.OrdinalIgnoreCase)
        ? holder
        : $"Copyright (c) {holder}";
    return Regex.Replace(text, @"^Copyright \(c\) <year> <[^>\r\n]+>[ .]*$", line, RegexOptions.Multiline);
}

IEnumerable<string> PackageRoots()
{
    yield return Environment.GetEnvironmentVariable("NUGET_PACKAGES") is { Length: > 0 } packages
        ? Path.GetFullPath(packages)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
    // Runtime packs can also come with the SDK, in dotnet\packs beside the runtime this tool runs on.
    var runtime = Path.GetDirectoryName(typeof(object).Assembly.Location);
    if (runtime is not null && Directory.GetParent(runtime)?.Parent?.Parent is { } dotnet) yield return Path.Combine(dotnet.FullName, "packs");
}

string? FindPackage(string id, string version)
{
    foreach (var root in roots)
    {
        // The NuGet cache folds ids and versions to lower case; dotnet\packs keeps the id's own case.
        foreach (var candidate in new[] { Path.Combine(root, id.ToLowerInvariant(), version.ToLowerInvariant()), Path.Combine(root, id, version) })
            if (Directory.Exists(candidate)) return candidate;
    }
    return null;
}

static bool IsLicenceFile(string name) =>
    name.StartsWith("LICENSE", StringComparison.OrdinalIgnoreCase) || name.StartsWith("LICENCE", StringComparison.OrdinalIgnoreCase);

static bool IsNoticeFile(string name) =>
    name.StartsWith("NOTICE", StringComparison.OrdinalIgnoreCase) || name.StartsWith("THIRD-PARTY-NOTICES", StringComparison.OrdinalIgnoreCase)
    || name.StartsWith("ThirdPartyNotices", StringComparison.OrdinalIgnoreCase);

static string Section(string title, string text) => $"===== {title} ====={Environment.NewLine}{Environment.NewLine}{text.Trim()}";

static string Rule() => Environment.NewLine + Environment.NewLine + new string('-', 80) + Environment.NewLine + Environment.NewLine;

static string FindRepoRoot()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "Bibliotaph.slnx"))) return dir.FullName;
    throw new InvalidOperationException("Run LicenseNotices from inside the Bibliotaph repository.");
}
