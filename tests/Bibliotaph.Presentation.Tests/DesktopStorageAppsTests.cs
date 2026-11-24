using System.Text.RegularExpressions;
using System.Xml.Linq;
using Bibliotaph.Presentation.Services;
using Bibliotaph.Storage;
using Bibliotaph.Storage.WebDav;

namespace Bibliotaph.Presentation.Tests;

public class DesktopStorageAppsTests
{
    /// <summary>
    /// The client IDs are assembly metadata, and <see cref="DesktopStorageApps"/> reads its own assembly's: if the
    /// class moves to another project, or the items to the app's, every OAuth provider quietly disappears from
    /// Settings while WebDAV still works.
    /// </summary>
    [Fact]
    public void Every_OAuth_provider_has_its_client_id_on_the_assembly_that_reads_it()
    {
        var keys = StorageMetadata().Select(item => item.Key).ToList();

        Assert.All(StorageKinds.All.OfType<OAuthStorageKind>(), kind =>
            Assert.Contains($"Storage.{kind.Id}.ClientId", keys));
    }

    [Fact]
    public void The_release_build_sets_every_storage_value()
    {
        var workflow = File.ReadAllText(Path.Combine(RepoRoot(), ".github", "workflows", "release.yml"));

        Assert.All(StorageMetadata(), item =>
            Assert.Matches(new Regex($@"^\s+{Regex.Escape(item.Property)}:", RegexOptions.Multiline), workflow));
    }

    [Fact]
    public void Only_providers_built_with_an_app_can_connect()
    {
        var apps = new DesktopStorageApps(new Dictionary<string, string>
        {
            ["Storage.onedrive.ClientId"] = "onedrive-app",
            ["Storage.googledrive.ClientId"] = "google-app",
            ["Storage.googledrive.ClientSecret"] = "google-secret",
        });

        Assert.True(apps.CanConnect(StorageKinds.Find("onedrive")!));
        Assert.True(apps.CanConnect(StorageKinds.Find("googledrive")!));
        Assert.False(apps.CanConnect(StorageKinds.Find("dropbox")!));
        Assert.True(apps.CanConnect(StorageKinds.Find(WebDavKind.Key)!));
        Assert.Equal(new("google-app", "google-secret"), apps.Find("googledrive"));
        Assert.Null(apps.Find("onedrive")!.ClientSecret);
    }

    [Fact]
    public void Dropbox_redirects_to_the_fixed_port()
    {
        var apps = new DesktopStorageApps(new Dictionary<string, string> { ["Storage.dropbox.ClientId"] = "dropbox-app" });

        Assert.Equal(DesktopStorageApps.FixedPort, apps.Find("dropbox")!.Port);
    }

    /// <summary>The Storage.* metadata items in the project <see cref="DesktopStorageApps"/> is built from, with the build property each is set from.</summary>
    static List<(string Key, string Property)> StorageMetadata()
    {
        var assembly = typeof(DesktopStorageApps).Assembly.GetName().Name!;
        var project = XDocument.Load(Path.Combine(RepoRoot(), "src", assembly, $"{assembly}.csproj"));
        return [.. project.Descendants("AssemblyMetadata")
            .Select(item => ((string)item.Attribute("Include")!, (string)item.Attribute("Value")!))
            .Where(item => item.Item1.StartsWith("Storage.", StringComparison.Ordinal))
            .Select(item => (item.Item1, Regex.Match(item.Item2, @"^\$\((\w+)\)$").Groups[1].Value))];
    }

    /// <summary>The checkout this test runs from: the folder holding Bibliotaph.slnx, above the test's output.</summary>
    static string RepoRoot()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
            if (File.Exists(Path.Combine(folder.FullName, "Bibliotaph.slnx"))) return folder.FullName;
        throw new InvalidOperationException($"No Bibliotaph.slnx above {AppContext.BaseDirectory}.");
    }
}
