using System.Reflection;
using Bibliotaph.Core;
using Bibliotaph.Presentation.Composition;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.Presentation.Tests;

public class DesktopCompositionTests
{
    // Each host service is a factory nothing calls: building checks only that every constructor can be satisfied.
    static ServiceCollection WithHostServices(IEnumerable<Type> hostServices)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        foreach (var type in hostServices)
            services.AddSingleton(type, _ => throw new NotSupportedException($"{type.Name} is the host's."));
        return services;
    }

    static void Build(IServiceCollection services)
    {
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
    }

    [Fact]
    public void Builds_with_only_the_host_services_it_names()
    {
        var services = WithHostServices(DesktopComposition.HostServices);
        services.AddDesktop(new AppPaths(Path.GetTempPath()));

        Build(services);
    }

    [Fact]
    public void Builds_with_stand_ins_for_a_test_run()
    {
        var services = WithHostServices(DesktopComposition.HostServices);
        services.AddDesktop(new AppPaths(Path.GetTempPath()), pilot: true, dev: true, new DesktopStandIns());

        Build(services);
    }

    // A host service whose every call fails: resolving the shared services then shows which they reach for in a
    // factory, which building alone can't see.
#pragma warning disable CA1852 // DispatchProxy makes a type derived from it, so it can't be sealed.
    class HostService : DispatchProxy
#pragma warning restore CA1852
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => throw new NotSupportedException(targetMethod?.Name);
    }

    [Fact]
    public async Task Every_service_it_registers_finds_what_it_needs()
    {
        var root = Directory.CreateTempSubdirectory("bibliotaph-composition-");
        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            foreach (var type in DesktopComposition.HostServices.Where(t => t.IsInterface))
                services.AddSingleton(type, DispatchProxy.Create(type, typeof(HostService)));
            services.AddSingleton(new AppPaths(root.FullName));
            services.AddSingleton(TimeProvider.System);
            services.AddSingleton<Bibliotaph.Presentation.ViewModels.OnlineLibrariesSection>(_ => throw new NotSupportedException());
            services.AddDesktop(new AppPaths(root.FullName));

            var missing = new List<string>();
            await using (var provider = services.BuildServiceProvider())
            {
                foreach (var type in services.Select(d => d.ServiceType).Where(t => !t.IsGenericTypeDefinition).Distinct())
                {
                    try
                    {
                        _ = provider.GetServices(type).ToList();
                    }
                    catch (Exception ex) when (Unregistered(ex) is { } name)
                    {
                        missing.Add($"{type.Name} needs {name}");
                    }
                    catch (Exception)
                    {
                        // A host stand-in refused a call; the registration itself is whole.
                    }
                }
            }
            Assert.Empty(missing);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            root.Delete(recursive: true);
        }
    }

    static string? Unregistered(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
            if (e is InvalidOperationException && e.Message.StartsWith("No service for type", StringComparison.Ordinal)) return e.Message;
        return null;
    }
}
