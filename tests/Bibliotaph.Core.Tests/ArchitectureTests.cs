using Bibliotaph.Core;

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
}
