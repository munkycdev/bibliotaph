using System.Diagnostics;
using System.Globalization;
using System.Windows;
using Bibliotaph.Core;
using Bibliotaph.Processing;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.App.Services;

/// <summary>
/// Whether the app started with --dev (slice 4l plan, choice 7), which shows Settings > Start over. Without it nothing
/// leads there, so nobody else finds a button that deletes everything.
/// </summary>
public sealed class DevMode(bool on)
{
    public bool IsOn { get; set; } = on;
}

/// <summary>
/// Settings > Start over, shown only with --dev while Bibliotaph is in development (to be removed before 1.0). Forgets
/// remembered PDF passwords and saved endpoint keys at once, then restarts; the new process waits for this one to exit and deletes the
/// library data (<see cref="DataPurge"/>) before it opens anything.
/// </summary>
public sealed class StartOver(AppPaths paths, IPasswordVault vault, ApiKeyVault keys, ILogger<StartOver> log)
{
    /// <summary><c>--wait-for &lt;process id&gt;</c>: the app that asked to start over, which must exit first.</summary>
    public const string WaitForArgument = "--wait-for";

    public static bool Confirm() => MessageBox.Show(Application.Current.MainWindow,
        "Bibliotaph will forget your library folders, its index, covers, settings, remembered PDF passwords and AI keys, " +
        "then restart as if newly installed.\n\nYour books and other files are not touched.",
        "Start over?", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) == MessageBoxResult.OK;

    public void Run()
    {
        var forgotten = vault.ForgetAll();
        keys.ForgetAll();
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
