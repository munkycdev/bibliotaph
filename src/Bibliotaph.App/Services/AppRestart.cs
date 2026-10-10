using System.Diagnostics;
using System.Globalization;
using System.Windows;
using Bibliotaph.Core;
using Bibliotaph.Processing;

namespace Bibliotaph.App.Services;

/// <summary>
/// Restarting Bibliotaph to finish a restore (slice 4j plan, choice 4), the way Start over restarts: the new process
/// is given the same data folder and waits for this one to exit before it swaps the restored catalog in
/// (<see cref="PendingRestore"/>), since Windows won't replace a database that is still open.
/// </summary>
public sealed class AppRestart(AppPaths paths)
{
    public void Run()
    {
        var restart = new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException("Bibliotaph can't find its own program file."))
        {
            UseShellExecute = false,
        };
        restart.ArgumentList.Add("--data-root");
        restart.ArgumentList.Add(paths.Root);
        restart.ArgumentList.Add(StartOver.WaitForArgument);
        restart.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        Process.Start(restart);
        Application.Current.Shutdown();
    }

    /// <summary>After the restart: what the restore did, or why the catalog in use was kept.</summary>
    public static void Tell(RestoreOutcome outcome)
    {
        var made = outcome.Request is { } request ? request.BackupCreatedUtc.ToLocalTime().ToString("d MMMM yyyy", CultureInfo.CurrentCulture) : null;
        if (!outcome.Restored)
        {
            MessageBox.Show(Application.Current.MainWindow,
                $"Bibliotaph couldn't restore the backup{(made is null ? "" : $" from {made}")}, so your catalog is as it was.\n\n{outcome.Problem}",
                "Restore", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var lines = new List<string> { $"Your library is back as it was on {made}." };
        if (outcome.Request is { IncludesIndex: false })
            lines.Add("Bibliotaph is reading your books again for search, in the background. Settings > Processing shows how far it has got.");
        if (outcome.Request is { OfflineFolders: > 0 } offline)
            lines.Add(offline.OfflineFolders == 1
                ? "One library folder wasn't found, so its books are marked offline. Settings > Library can point it to its new place."
                : $"{offline.OfflineFolders} library folders weren't found, so their books are marked offline. Settings > Library can point each to its new place.");
        lines.Add("Passwords for protected PDFs and AI keys aren't in backups: Bibliotaph asks for them again when it needs them.");
        if (outcome.PreviousCatalogBackup is { } kept) lines.Add($"The catalog you had before is kept in the backups folder as {System.IO.Path.GetFileName(kept)}.");
        MessageBox.Show(Application.Current.MainWindow, string.Join("\n\n", lines), "Restored", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
