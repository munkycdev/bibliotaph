using System.Globalization;
using System.ComponentModel;
using AppKit;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Presentation.Services;
using Bibliotaph.Presentation.ViewModels;
using Bibliotaph.Processing;
using Foundation;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.Mac.Platform;

/// <summary>
/// Light or dark as the user picks in Settings, or as macOS is set. The choice is the window's appearance, which every
/// AppKit view follows.
/// </summary>
public sealed class MacTheme(SettingsStore settings) : IThemeService
{
    public ThemePreference Preference { get; private set; }

    public bool IsDark => Preference switch
    {
        ThemePreference.Dark => true,
        ThemePreference.Light => false,
        _ => NSApplication.SharedApplication.EffectiveAppearance.Name.Contains("Dark", StringComparison.Ordinal),
    };

    public bool IsHighContrast => NSWorkspace.SharedWorkspace.AccessibilityDisplayShouldIncreaseContrast;

    public event EventHandler? Changed;

    public async Task InitializeAsync()
    {
        Preference = Enum.TryParse<ThemePreference>(await settings.GetAsync(SettingKeys.Appearance), out var saved) ? saved : ThemePreference.System;
        Apply();
    }

    public async Task SetPreferenceAsync(ThemePreference preference)
    {
        Preference = preference;
        Apply();
        await settings.SetAsync(SettingKeys.Appearance, preference.ToString());
    }

    public Task ToggleAsync() => SetPreferenceAsync(IsDark ? ThemePreference.Light : ThemePreference.Dark);

    void Apply()
    {
        NSApplication.SharedApplication.Appearance = Preference switch
        {
            ThemePreference.Dark => NSAppearance.GetAppearance(NSAppearance.NameDarkAqua),
            ThemePreference.Light => NSAppearance.GetAppearance(NSAppearance.NameAqua),
            _ => null,
        };
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>Updates come as a new build for now; nothing is downloaded in the background.</summary>
public sealed class NoUpdates : IUpdateService
{
    public bool IsReady => false;

    public string ReadyText => "";

    public event PropertyChangedEventHandler? PropertyChanged
    {
        add { }
        remove { }
    }

    public void Start()
    {
    }

    public void RestartToUpdate()
    {
    }
}

/// <summary>Start over's part of the secrets: everything Bibliotaph saved, wherever it keeps them.</summary>
public interface IForgetsSecrets
{
    int ForgetEverything();
}

/// <summary>
/// Restart relaunches the bundle, which waits for this copy to have closed its databases before it starts, as a
/// restore swaps them in then. Start over (with --dev) forgets the secrets now and asks the next start to delete the
/// library data before it opens anything, as on Windows.
/// </summary>
public sealed class MacAppLifetime(AppPaths paths, IForgetsSecrets secrets, ILogger<MacAppLifetime> log) : IAppLifetime
{
    public const string WaitFor = "--wait-for";

    public void Restart() => Relaunch();

    void Relaunch(params string[] more)
    {
        List<string> arguments = ["-n", NSBundle.MainBundle.BundlePath, "--args", WaitFor, Environment.ProcessId.ToString(CultureInfo.InvariantCulture), .. more];
        // Only a data folder of its own is named, as naming one turns off what an everyday start does.
        if (paths.Root != AppPaths.ForCurrentUser().Root) arguments.AddRange(["--data-root", paths.Root]);
        using var process = System.Diagnostics.Process.Start("/usr/bin/open", arguments);
        NSApplication.SharedApplication.Terminate(null);
    }

    /// <summary>After a restart, waits for the copy that asked for it to have quit.</summary>
    public static async Task WaitForAsync(string[] args)
    {
        var at = Array.IndexOf(args, WaitFor);
        if (at < 0 || at + 1 >= args.Length || !int.TryParse(args[at + 1], CultureInfo.InvariantCulture, out var id)) return;
        try
        {
            using var earlier = System.Diagnostics.Process.GetProcessById(id);
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await earlier.WaitForExitAsync(limit.Token);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or OperationCanceledException)
        {
            // Gone already, or still closing after the wait: the restore checks the databases itself.
        }
    }

    /// <summary>After the restart: what the restore did, or why the catalog in use was kept.</summary>
    public static void Tell(RestoreOutcome outcome, NSWindow? window)
    {
        var made = outcome.Request is { } request ? request.BackupCreatedUtc.ToLocalTime().ToString("d MMMM yyyy", CultureInfo.CurrentCulture) : null;
        var alert = new NSAlert();
        if (!outcome.Restored)
        {
            alert.AlertStyle = NSAlertStyle.Warning;
            alert.MessageText = "Restore";
            alert.InformativeText = $"Bibliotaph couldn't restore the backup{(made is null ? "" : $" from {made}")}, so your catalog is as it was.\n\n{outcome.Problem}";
        }
        else
        {
            var lines = new List<string> { $"Your library is back as it was on {made}." };
            if (outcome.Request is { IncludesIndex: false })
                lines.Add("Bibliotaph is reading your books again for search, in the background. Settings › Processing shows how far it has got.");
            if (outcome.Request is { OfflineFolders: > 0 } offline)
                lines.Add(offline.OfflineFolders == 1
                    ? "One library folder wasn't found, so its books are marked offline. Settings › Library can point it to its new place."
                    : $"{offline.OfflineFolders} library folders weren't found, so their books are marked offline. Settings › Library can point each to its new place.");
            lines.Add("Passwords for protected PDFs and AI keys aren't in backups: Bibliotaph asks for them again when it needs them.");
            if (outcome.PreviousCatalogBackup is { } kept) lines.Add($"The catalog you had before is kept in the backups folder as {Path.GetFileName(kept)}.");
            alert.MessageText = "Restored";
            alert.InformativeText = string.Join("\n\n", lines);
        }
        // A note doesn't stop the app quitting.
        alert.Window.PreventsApplicationTerminationWhenModal = false;
        if (window is not null) alert.BeginSheet(window, _ => alert.Dispose());
        else alert.RunModal();
    }

    public void StartOver()
    {
        var forgotten = secrets.ForgetEverything();
        DataPurge.Request(paths);
        log.LogInformation("Starting over: forgot {Count} saved secrets; restarting to delete the library data", forgotten);
        // Start over is reached only with --dev, so the new start keeps it.
        Relaunch("--dev");
    }
}

/// <summary>About shows the standard panel; Keyboard Shortcuts and Test with a Book are the Mac's own.</summary>
public sealed class MacAppDialogs(IUserPrompts prompts, AiService ai) : IAppDialogs
{
    public void ShowAbout() => NSApplication.SharedApplication.OrderFrontStandardAboutPanel(null);

    public void ShowShortcuts() => Views.ShortcutsWindow.Show();

    public void TestModelServer(string endpoint, string model) =>
        Views.AiTestSheet.Show(new AiTestViewModel(ai, (progress, ct) => ai.TestAsync(endpoint, model, progress, ct), model));

    public void TestBuiltInModel(string model) =>
        prompts.Tell("The built-in AI isn't available on the Mac yet.", "Bibliotaph", PromptKind.Information);
}

/// <summary>Apple silicon shares memory between the CPU and GPU, so there is no separate graphics memory to report.</summary>
public sealed class MacGraphicsMemory : IGraphicsMemory
{
    public long? Bytes => null;
}

/// <summary>A PDF's password, asked for in an alert with a secure field and the same choices as on Windows.</summary>
public sealed class MacPasswordPrompt : IPasswordPrompt
{
    public EnteredPassword? Ask(string title, bool retry)
    {
        using var field = new NSSecureTextField(new CoreGraphics.CGRect(0, 0, 260, 24));
        using var remember = NSButton.CreateCheckbox("Remember this password", () => { });
        using var searchable = NSButton.CreateCheckbox("Make its text searchable", () => { });
        searchable.State = NSCellStateValue.On;
        var stack = NSStackView.FromViews([field, remember, searchable]);
        stack.Orientation = NSUserInterfaceLayoutOrientation.Vertical;
        stack.Alignment = NSLayoutAttribute.Leading;
        stack.Spacing = 8;
        stack.SetFrameSize(new CoreGraphics.CGSize(260, 84));
        using var alert = new NSAlert
        {
            MessageText = retry ? "That password didn't open it" : "This PDF needs a password",
            InformativeText = title,
            AccessoryView = stack,
        };
        alert.AddButton("Open");
        alert.AddButton("Cancel");
        alert.Window.InitialFirstResponder = field;
        if (alert.RunModal() != (nint)(long)NSAlertButtonReturn.First || field.StringValue.Length == 0) return null;
        return new EnteredPassword(field.StringValue, remember.State == NSCellStateValue.On, searchable.State == NSCellStateValue.On);
    }
}

/// <summary>Bibliotaph's own browser is WebKit, which every Mac has, so books owned online can always open in it.</summary>
public sealed class MacInAppBrowser : IInAppBrowser
{
    public bool IsInstalled => true;
}

/// <summary>The e-book reader isn't on the Mac yet.</summary>
public sealed class NoEbookBrowser : IEbookBrowser;

