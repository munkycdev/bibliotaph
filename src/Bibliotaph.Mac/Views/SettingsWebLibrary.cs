using AppKit;
using Bibliotaph.Mac.Binding;
using Bibliotaph.Presentation.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Bibliotaph.Mac.Views;

/// <summary>
/// Settings › Web Library, as Windows' Devices: connecting this Mac to the user's web library, which approves it in the
/// browser with a code to check, then how its sync is going, and how much room copies of web books may take here.
/// </summary>
public sealed partial class SettingsWindow
{
    NSScrollView WebLibraryTab()
    {
        var devices = _services.GetRequiredService<DevicesSectionViewModel>();
        var bindings = Load(devices);
        var form = new Form();

        var connection = form.Section("bibliotaph.online",
            "Connected, this Mac keeps its folders, corrections, collections and binders in step with your other devices, and looks books up through the web library.");
        var connect = Button("Connect…", devices.ConnectCommand, bindings);
        connect.KeyEquivalent = "\r";
        var disconnect = Button("Disconnect…", devices.DisconnectCommand, bindings);
        var status = connection.Row("This Mac", connect, disconnect);

        var code = NSTextField.CreateLabel("");
        code.Font = NSFont.MonospacedSystemFont(22, NSFontWeight.Medium)!;
        code.Selectable = true;
        var waiting = connection.Row("Code to check", code, Button("Open the Page Again", devices.OpenPageCommand, bindings), Button("Cancel", devices.CancelCommand, bindings));
        waiting.ShowDetail("Approve this Mac in the page that opened in your browser, checking it shows this code.");
        var sync = connection.Row("Sync");

        var copies = form.Section("Copies of web books",
            "Books that are only in your web library are copied here as you open them, up to this much room; the least used go first.");
        var room = FormText.Choices(devices.RoomChoices.Select(gb => $"{gb} GB"), at =>
        {
            if (at >= 0 && at < devices.RoomChoices.Count) devices.RoomGb = devices.RoomChoices[at];
        });
        // Free Up Space deletes the copies not kept offline, as on Windows; they come back as they're opened.
        var free = Button("Free Up Space", devices.FreeUpSpaceCommand, bindings);
        var roomRow = copies.Row("Room on this Mac", room, free);

        bindings.Properties(devices, [nameof(DevicesSectionViewModel.IsConnected), nameof(DevicesSectionViewModel.IsWaiting),
            nameof(DevicesSectionViewModel.Code), nameof(DevicesSectionViewModel.StatusLine), nameof(DevicesSectionViewModel.SyncLine),
            nameof(DevicesSectionViewModel.Note)], () =>
        {
            status.ShowDetail(devices.StatusLine);
            connect.Hidden = !devices.CanConnect;
            disconnect.Hidden = !devices.IsConnected;
            code.StringValue = devices.Code ?? "";
            waiting.Hidden = !devices.IsWaiting || devices.Code is null;
            sync.ShowDetail(devices.SyncLine);
            sync.Hidden = devices.SyncLine is null;
            connection.Note(devices.Note ?? "Connected, this Mac keeps its folders, corrections, collections and binders in step with your other devices, and looks books up through the web library.");
            connection.Footer.TextColor = devices.Note is null ? NSColor.SecondaryLabel : NSColor.SystemOrange;
        });
        bindings.Properties(devices, [nameof(DevicesSectionViewModel.ShowsCopies), nameof(DevicesSectionViewModel.CopiesLine), nameof(DevicesSectionViewModel.RoomGb)], () =>
        {
            copies.View.Hidden = !devices.ShowsCopies;
            roomRow.ShowDetail(devices.CopiesLine);
            room.SelectItem(devices.RoomChoices.ToList().IndexOf(devices.RoomGb));
        });
        return Pane(form);
    }
}
