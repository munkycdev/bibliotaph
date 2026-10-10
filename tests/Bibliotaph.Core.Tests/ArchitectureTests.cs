using System.Text.RegularExpressions;

namespace Bibliotaph.Core.Tests;

public class ArchitectureTests
{
    [Fact]
    public void Core_references_only_the_base_class_library()
    {
        var references = typeof(ContentHash).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();

        Assert.All(references, name => Assert.True(name.StartsWith("System", StringComparison.Ordinal) || name == "netstandard",
            $"Bibliotaph.Core must reference nothing outside the BCL, but references {name}."));
    }

    [Fact]
    public void App_data_lives_in_local_app_data_not_a_synced_folder()
    {
        var paths = AppPaths.ForCurrentUser();

        Assert.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), paths.Root, StringComparison.Ordinal);
        Assert.Equal(Path.Combine(paths.Root, "catalog.db"), paths.CatalogDatabase);
        Assert.Equal(Path.Combine(paths.Root, "index.db"), paths.IndexDatabase);
        Assert.Contains(paths.Backups, paths.Directories);
    }

    /// <summary>
    /// Velopack installs into %LOCALAPPDATA%\&lt;pack id&gt; and deletes that folder on uninstall (slice 4l plan, choice 1):
    /// were it the data folder, or either inside the other, uninstalling would delete the library.
    /// </summary>
    [Fact]
    public void The_install_folder_is_apart_from_the_data_folder()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var data = Path.Combine(localAppData, AppPaths.FolderName);
        var install = Path.Combine(localAppData, AppPaths.PackId);

        Assert.Equal(data, AppPaths.ForCurrentUser().Root);
        // Windows paths ignore case.
        Assert.False(string.Equals(AppPaths.FolderName, AppPaths.PackId, StringComparison.OrdinalIgnoreCase),
            "The pack id and the data folder have the same name.");
        Assert.False(IsWithin(data, install), $"The data folder {data} is inside the install folder {install}.");
        Assert.False(IsWithin(install, data), $"The install folder {install} is inside the data folder {data}.");
    }

    [Fact]
    public void The_release_workflow_packs_with_the_pack_id()
    {
        var workflow = File.ReadAllText(Path.Combine(RepoRoot(), ".github", "workflows", "release.yml"));
        var ids = Regex.Matches(workflow, @"--packId\s+(\S+)").Select(m => m.Groups[1].Value).ToList();

        Assert.NotEmpty(ids);
        Assert.All(ids, id => Assert.Equal(AppPaths.PackId, id));
    }

    /// <summary>True when <paramref name="path"/> is <paramref name="folder"/> or somewhere inside it.</summary>
    static bool IsWithin(string path, string folder)
    {
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
        return (Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) + Path.DirectorySeparatorChar)
            .StartsWith(parent, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The checkout this test runs from: the folder holding Bibliotaph.slnx, above the test's output.</summary>
    static string RepoRoot()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
            if (File.Exists(Path.Combine(folder.FullName, "Bibliotaph.slnx"))) return folder.FullName;
        throw new InvalidOperationException($"No Bibliotaph.slnx above {AppContext.BaseDirectory}.");
    }
}
