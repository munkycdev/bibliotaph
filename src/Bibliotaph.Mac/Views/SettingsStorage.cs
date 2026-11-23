using AppKit;
using Bibliotaph.Mac.Binding;
using Bibliotaph.Presentation.ViewModels;

namespace Bibliotaph.Mac.Views;

/// <summary>
/// Settings > Library's Storage, above the folders, as on Windows: the online storage connected on this Mac (OneDrive,
/// Dropbox, Google Drive, or a WebDAV server such as Nextcloud or a NAS), what each was allowed, connecting another in
/// the browser or by a WebDAV folder's address and password, connecting one again, and disconnecting. Add from Storage
/// picks a folder in it, which is added as any folder is.
/// </summary>
public sealed partial class SettingsWindow
{
    void Storage(Form form, LibrarySectionViewModel library, Bindings bindings)
    {
        var storage = library.Storage;
        var section = form.Section("Storage",
            "Connect the online storage where your books are, then add folders from it to your library. Bibliotaph only reads them there: it never changes, moves or deletes anything in your storage.");
        var rows = new Bindings();
        bindings.Add(() => rows.Dispose());

        // Connecting: what it waits on, with Cancel; the WebDAV form; the Connect buttons.
        var status = FormText.Note("", 12);
        var cancel = NSButton.CreateButton("Cancel", () => { });
        bindings.Command(cancel, storage.CancelConnectingCommand);
        var address = new NSTextField { PlaceholderString = "https://cloud.example.com/remote.php/dav/files/you/Books" };
        address.Changed += (_, _) => storage.WebDavAddress = address.StringValue;
        var user = new NSTextField { PlaceholderString = "User name" };
        user.Changed += (_, _) => storage.WebDavUser = user.StringValue;
        var password = new NSSecureTextField { PlaceholderString = "Password" };
        password.Changed += (_, _) => storage.WebDavPassword = password.StringValue;
        foreach (var field in new[] { address, user, (NSTextField)password }) field.WidthAnchor.ConstraintEqualTo(360).Active = true;
        var webDavProblem = FormText.Note("", 11);
        webDavProblem.TextColor = NSColor.SystemOrange;
        var connectWebDav = NSButton.CreateButton("Connect", () => { });
        connectWebDav.KeyEquivalent = "\r";
        bindings.Command(connectWebDav, storage.ConnectWebDavCommand);
        var closeWebDav = NSButton.CreateButton("Cancel", () => { });
        bindings.Command(closeWebDav, storage.CloseWebDavCommand);
        var webDavHelp = FormText.Note(
            "For Nextcloud, ownCloud, a NAS or any WebDAV server, on the internet or your own network. Give the address of the folder your books are in, or one above it. "
            + "Use an app password made for Bibliotaph where your server offers one (in Nextcloud, Personal settings › Security › Create new app password), so you can take it back without changing your own.",
            11);
        webDavHelp.PreferredMaxLayoutWidth = 560;
        var webDav = NSStackView.FromViews([webDavHelp, address, user, password, webDavProblem, NSStackView.FromViews([closeWebDav, connectWebDav])]);
        webDav.Orientation = NSUserInterfaceLayoutOrientation.Vertical;
        webDav.Alignment = NSLayoutAttribute.Leading;
        webDav.Spacing = 8;
        var connect = new NSStackView { Spacing = 8 };

        void Refresh()
        {
            rows.Dispose();
            rows = new Bindings();
            section.Clear();
            foreach (var account in storage.Accounts)
            {
                var again = NSButton.CreateButton("Connect Again", () => storage.ConnectAgainCommand.Execute(account));
                again.Hidden = !account.NeedsReconnect;
                var disconnect = NSButton.CreateButton("Disconnect", () => storage.DisconnectCommand.Execute(account));
                disconnect.ToolTip = "Disconnecting keeps your notes, collections and corrections. Its books come back when you connect it again.";
                var detail = account.Allowed.Length > 0 ? $"{account.Detail}. {account.Allowed}" : account.Detail;
                var row = section.Row(account.Name, detail, again, disconnect);
                row.ToolTip = account.Scopes ?? "";
            }
            if (!storage.HasAccounts) section.Row("No storage connected yet.");
            foreach (var view in connect.ArrangedSubviews) view.RemoveFromSuperview();
            foreach (var option in storage.Options)
            {
                var button = NSButton.CreateButton(option.Label, () => { });
                rows.Command(button, storage.ConnectCommand, () => option);
                if (option.IsPrimary) button.TintProminence = NSTintProminence.Primary;
                connect.AddArrangedSubview(button);
            }
            section.Wide(connect);
            var waiting = section.Wide(NSStackView.FromViews([status, cancel]));
            rows.Properties(storage, [nameof(StorageAccountsViewModel.Status), nameof(StorageAccountsViewModel.IsConnecting)], () =>
            {
                status.StringValue = storage.Status;
                waiting.Hidden = !storage.HasStatus;
                cancel.Hidden = !storage.IsConnecting;
            });
            var form = section.Wide(webDav);
            rows.Properties(storage, [nameof(StorageAccountsViewModel.IsWebDavOpen), nameof(StorageAccountsViewModel.WebDavProblem)], () =>
            {
                form.Hidden = !storage.IsWebDavOpen;
                connect.Hidden = storage.IsWebDavOpen;
                webDavProblem.StringValue = storage.WebDavProblem;
                webDavProblem.Hidden = !storage.HasWebDavProblem;
                if (!storage.IsWebDavOpen) address.StringValue = user.StringValue = password.StringValue = "";
            });
        }
        bindings.Collection(storage.Accounts, _ => Refresh());
        bindings.Property(storage, nameof(StorageAccountsViewModel.Options), Refresh);
        Refresh();
        bindings.Add(storage.Leave);
    }

    /// <summary>A folder in connected storage, as Windows' picker: the account, the way down from its top, and Use This Folder.</summary>
    static void FollowPicker(LibrarySectionViewModel library, Bindings bindings, Func<NSWindow?> window)
    {
        (Sheet Sheet, Bindings Bindings)? showing = null;
        void Close()
        {
            if (showing is not { } old) return;
            old.Bindings.Dispose();
            old.Sheet.Close();
            old.Sheet.Dispose();
            showing = null;
        }
        bindings.Property(library, nameof(LibrarySectionViewModel.Picker), () =>
        {
            Close();
            if (library.Picker is not { } picker || window() is not { } host) return;
            var sheet = new Sheet(460);
            var own = new Bindings();
            sheet.Text("Add a folder from storage", size: 15).Font = NSFont.SystemFontOfSize(15, NSFontWeight.Semibold)!;
            if (picker.HasManyAccounts)
            {
                var accounts = FormText.Choices(picker.Accounts.Select(a => a.Name), at =>
                {
                    if (at >= 0 && at < picker.Accounts.Count) picker.Account = picker.Accounts[at];
                });
                if (picker.Account is { } chosen) accounts.SelectItem(picker.Accounts.ToList().IndexOf(chosen));
                sheet.AddWide(accounts);
            }
            var crumbs = new NSStackView { Spacing = 2 };
            sheet.AddWide(crumbs);
            var list = new NSStackView { Orientation = NSUserInterfaceLayoutOrientation.Vertical, Alignment = NSLayoutAttribute.Leading, Spacing = 2 };
            var document = new FlippedView { TranslatesAutoresizingMaskIntoConstraints = false };
            list.TranslatesAutoresizingMaskIntoConstraints = false;
            document.AddSubview(list);
            var scroll = new NSScrollView { HasVerticalScroller = true, DocumentView = document, BorderType = NSBorderType.BezelBorder };
            NSLayoutConstraint.ActivateConstraints(
            [
                list.TopAnchor.ConstraintEqualTo(document.TopAnchor, 6),
                list.LeadingAnchor.ConstraintEqualTo(document.LeadingAnchor, 6),
                list.TrailingAnchor.ConstraintEqualTo(document.TrailingAnchor, -6),
                list.BottomAnchor.ConstraintEqualTo(document.BottomAnchor, -6),
                document.WidthAnchor.ConstraintEqualTo(scroll.ContentView.WidthAnchor),
            ]);
            scroll.HeightAnchor.ConstraintEqualTo(240).Active = true;
            sheet.AddWide(scroll);
            var note = sheet.Text("", secondary: true, size: 11);
            var cancel = NSButton.CreateButton("Cancel", () => picker.CancelCommand.Execute(null));
            cancel.KeyEquivalent = "\u001b";
            var use = NSButton.CreateButton("Use This Folder", () => { });
            own.Command(use, picker.UseCommand);
            sheet.Buttons(cancel, use);
            own.Properties(picker, [nameof(StorageFolderPickerViewModel.Crumbs), nameof(StorageFolderPickerViewModel.Folders),
                nameof(StorageFolderPickerViewModel.IsLoading), nameof(StorageFolderPickerViewModel.Problem)], () =>
            {
                foreach (var view in crumbs.ArrangedSubviews) view.RemoveFromSuperview();
                for (var i = 0; i < picker.Crumbs.Count; i++)
                {
                    var step = picker.Crumbs[i];
                    if (i > 0) crumbs.AddArrangedSubview(NSTextField.CreateLabel("›"));
                    var crumb = NSButton.CreateButton(step.Name, () => picker.OpenCommand.Execute(step));
                    crumb.Bordered = false;
                    crumb.ContentTintColor = i == picker.Crumbs.Count - 1 ? NSColor.Label : NSColor.ControlAccent;
                    crumbs.AddArrangedSubview(crumb);
                }
                foreach (var view in list.ArrangedSubviews) view.RemoveFromSuperview();
                foreach (var folder in picker.Folders)
                {
                    var open = NSButton.CreateButton(folder.Name, () => picker.OpenCommand.Execute(folder));
                    open.Bordered = false;
                    open.Image = NSImage.GetSystemSymbol("folder", null)!;
                    open.ImagePosition = NSCellImagePosition.ImageLeading;
                    open.Alignment = NSTextAlignment.Left;
                    list.AddArrangedSubview(open);
                }
                note.StringValue = picker.IsLoading ? "Looking…" : picker.HasProblem ? picker.Problem : picker.IsEmpty ? "No folders inside this one." : "";
                note.TextColor = picker.HasProblem ? NSColor.SystemOrange : NSColor.SecondaryLabel;
                note.Hidden = note.StringValue.Length == 0;
            });
            sheet.Show(host);
            showing = (sheet, own);
        });
        bindings.Add(Close);
    }
}
