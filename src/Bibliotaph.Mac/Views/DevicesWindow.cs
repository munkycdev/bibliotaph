using AppKit;
using Bibliotaph.Mac.Binding;
using Bibliotaph.Presentation.Services;
using Bibliotaph.Presentation.ViewModels;
using CoreGraphics;
using Microsoft.Extensions.DependencyInjection;

namespace Bibliotaph.Mac.Views;

/// <summary>
/// Settings › Devices as a small window of its own: connecting this Mac to the user's web library, which approves it in
/// the browser, then how its library sync is going. Connected, the web library's folders and sync come to this Mac as
/// they do to a connected PC.
/// </summary>
public sealed class DevicesWindow : NSWindowController
{
    readonly IServiceProvider _services;
    DevicesSectionViewModel? _devices;
    Bindings? _bindings;

    public DevicesWindow(IServiceProvider services)
        : base(new NSWindow(new CGRect(0, 0, 480, 320), NSWindowStyle.Titled | NSWindowStyle.Closable, NSBackingStore.Buffered, false))
    {
        _services = services;
        var window = Window!;
        window.Title = "Devices";
        window.ReleaseWhenClosed(false);
        // Tab goes through the controls as they are now, as pages and panes change them.
        window.AutorecalculatesKeyViewLoop = true;
        window.BackgroundColor = Palette.Background;
        window.WillClose += (_, _) => Closed();
    }

    public void Open()
    {
        if (_devices is null) Build();
        Window!.Center();
        Window.MakeKeyAndOrderFront(null);
    }

    void Build()
    {
        var devices = _services.GetRequiredService<DevicesSectionViewModel>();
        var bindings = new Bindings();
        _devices = devices;
        _bindings = bindings;

        var heading = NSTextField.CreateLabel("Your web library");
        heading.Font = NSFont.SystemFontOfSize(17, NSFontWeight.Semibold)!;
        var status = NSTextField.CreateWrappingLabel("");
        var sync = NSTextField.CreateWrappingLabel("");
        sync.TextColor = NSColor.SecondaryLabel;
        var code = NSTextField.CreateLabel("");
        code.Font = NSFont.MonospacedSystemFont(26, NSFontWeight.Medium)!;
        var waiting = NSTextField.CreateWrappingLabel("Approve this Mac in the page that opened in your browser, checking it shows this code.");
        waiting.TextColor = NSColor.SecondaryLabel;
        var note = NSTextField.CreateWrappingLabel("");
        note.TextColor = NSColor.SystemOrange;
        var connect = NSButton.CreateButton("Connect…", () => { });
        connect.KeyEquivalent = "\r";
        var openPage = NSButton.CreateButton("Open the page again", () => { });
        var cancel = NSButton.CreateButton("Cancel", () => { });
        var disconnect = NSButton.CreateButton("Disconnect", () => { });
        var buttons = NSStackView.FromViews([connect, openPage, cancel, disconnect]);
        buttons.Spacing = 8;

        var stack = NSStackView.FromViews([heading, status, sync, code, waiting, note, buttons]);
        stack.Orientation = NSUserInterfaceLayoutOrientation.Vertical;
        stack.Alignment = NSLayoutAttribute.Leading;
        stack.Spacing = 10;
        stack.EdgeInsets = new NSEdgeInsets(20, 22, 20, 22);
        foreach (var label in new[] { status, sync, waiting, note }) label.PreferredMaxLayoutWidth = 430;
        Window!.ContentView = stack;

        bindings.Command(connect, devices.ConnectCommand);
        bindings.Command(openPage, devices.OpenPageCommand);
        bindings.Command(cancel, devices.CancelCommand);
        bindings.Command(disconnect, devices.DisconnectCommand);
        bindings.Property(devices, nameof(DevicesSectionViewModel.StatusLine), () => status.StringValue = devices.StatusLine ?? "");
        bindings.Property(devices, nameof(DevicesSectionViewModel.SyncLine), () =>
        {
            sync.StringValue = devices.SyncLine ?? "";
            sync.Hidden = devices.SyncLine is null;
        });
        bindings.Property(devices, nameof(DevicesSectionViewModel.Note), () =>
        {
            note.StringValue = devices.Note ?? "";
            note.Hidden = devices.Note is null;
        });
        bindings.Properties(devices, [nameof(DevicesSectionViewModel.IsConnected), nameof(DevicesSectionViewModel.IsWaiting), nameof(DevicesSectionViewModel.Code)], () =>
        {
            code.StringValue = devices.Code ?? "";
            code.Hidden = devices.Code is null;
            waiting.Hidden = !devices.IsWaiting;
            connect.Hidden = !devices.CanConnect;
            openPage.Hidden = !devices.IsWaiting;
            cancel.Hidden = !devices.IsWaiting;
            disconnect.Hidden = !devices.IsConnected;
            Fit(stack);
        });
        bindings.Properties(devices, [nameof(DevicesSectionViewModel.StatusLine), nameof(DevicesSectionViewModel.SyncLine), nameof(DevicesSectionViewModel.Note)],
            () => Fit(stack));
        devices.LoadAsync().FireAndForget();
    }

    /// <summary>The window grows and shrinks with what it says, as lines come and go.</summary>
    void Fit(NSView content)
    {
        content.LayoutSubtreeIfNeeded();
        Window!.SetContentSize(new CGSize(480, Math.Max(140, content.FittingSize.Height)));
    }

    /// <summary>The window closed, for the sidebar to check whether this Mac is connected now.</summary>
    public event EventHandler? Finished;

    /// <summary>A connection still waiting for approval stops with the window; Connect asks afresh.</summary>
    void Closed()
    {
        Finished?.Invoke(this, EventArgs.Empty);
        _devices?.Unload();
        _bindings?.Dispose();
        _devices = null;
        _bindings = null;
    }
}
