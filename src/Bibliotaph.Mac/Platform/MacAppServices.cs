using System.ComponentModel;
using AppKit;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Presentation.Services;
using Bibliotaph.Presentation.ViewModels;
using Bibliotaph.Processing;
using Foundation;

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

/// <summary>Restart relaunches the bundle; Start over isn't offered on the Mac yet.</summary>
public sealed class MacAppLifetime(IUserPrompts prompts) : IAppLifetime
{
    public void Restart()
    {
        var bundle = NSBundle.MainBundle.BundlePath;
        using var process = System.Diagnostics.Process.Start("/usr/bin/open", ["-n", bundle]);
        NSApplication.SharedApplication.Terminate(null);
    }

    public void StartOver() =>
        prompts.Tell("Start over isn't available on the Mac yet.", "Bibliotaph", PromptKind.Information);
}

/// <summary>About shows the standard panel; the rest are Windows popups the Mac doesn't have yet.</summary>
public sealed class MacAppDialogs(IUserPrompts prompts) : IAppDialogs
{
    public void ShowAbout() => NSApplication.SharedApplication.OrderFrontStandardAboutPanel(null);

    public void ShowShortcuts() => prompts.Tell("Keyboard shortcuts are listed in the menus.", "Bibliotaph", PromptKind.Information);

    public void TestModelServer(string endpoint, string model) =>
        prompts.Tell("Testing a model server isn't available on the Mac yet.", "Bibliotaph", PromptKind.Information);

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

/// <summary>Importing from online libraries needs the in-app browser, which the Mac doesn't have yet.</summary>
public sealed class NoInAppBrowser : IInAppBrowser
{
    public bool IsInstalled => false;
}

/// <summary>The e-book reader isn't on the Mac yet.</summary>
public sealed class NoEbookBrowser : IEbookBrowser;

/// <summary>Settings › Online libraries, which on Windows imports through the in-app browser; that isn't on the Mac yet.</summary>
public sealed class MacOnlineLibraries(IUserPrompts prompts) : OnlineLibrariesSection
{
    public override SettingsSection Section => SettingsSection.OnlineLibraries;

    public override string Label => "Online libraries";

    public override void OpenLibraryPage(IOnlineLibraryReader reader) =>
        prompts.Tell("Online libraries can't be imported on the Mac yet.", "Bibliotaph", PromptKind.Information);
}
