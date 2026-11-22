using System.Diagnostics;
using System.Globalization;
using System.Windows;
using Bibliotaph.Core;
using Bibliotaph.Processing;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.App.Services;

/// <summary>
/// Settings > Start over, shown only with --dev, as a development aid. Forgets
/// remembered PDF passwords, saved endpoint keys and the web library key at once, then restarts; the new process waits for this one to exit and deletes the
/// library data (<see cref="DataPurge"/>) before it opens anything.
/// </summary>
public sealed class StartOver(AppPaths paths, IPasswordVault vault, ApiKeyVault keys, IWebLibraryKey webKey, ILogger<StartOver> log)
{
    /// <summary><c>--wait-for &lt;process id&gt;</c>: the app that asked to start over, which must exit first.</summary>
    public const string WaitForArgument = "--wait-for";

    public void Run()
    {
        var forgotten = vault.ForgetAll();
        keys.ForgetAll();
        webKey.Forget();
        DataPurge.Request(paths);
        log.LogInformation("Starting over: forgot {Count} remembered passwords; restarting to delete the library data", forgotten);

        var restart = new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException("Bibliotaph can't find its own program file."))
        {
            UseShellExecute = false,
        };
        restart.ArgumentList.Add("--data-root");
        restart.ArgumentList.Add(paths.Root);
        restart.ArgumentList.Add(WaitForArgument);
        restart.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        // Start over is reached only with --dev, so the new start keeps it.
        restart.ArgumentList.Add("--dev");
        Process.Start(restart);
        Application.Current.Shutdown();
    }

    /// <summary>
    /// At startup: waits for the app that asked to start over to exit, then deletes the library data if asked.
    /// Throws when a file is still locked; the request stands, so the next start tries again.
    /// </summary>
    public static void FinishIfRequested(AppPaths paths, string[] args)
    {
        var at = Array.IndexOf(args, WaitForArgument);
        if (at >= 0 && at + 1 < args.Length && int.TryParse(args[at + 1], CultureInfo.InvariantCulture, out var id))
        {
            try
            {
                using var previous = Process.GetProcessById(id);
                previous.WaitForExit(TimeSpan.FromSeconds(30));
            }
            catch (ArgumentException)
            {
                // Already gone.
            }
        }
        if (DataPurge.RunIfRequested(paths)) Serilog.Log.Information("Started over: the library data was deleted");
    }
}
