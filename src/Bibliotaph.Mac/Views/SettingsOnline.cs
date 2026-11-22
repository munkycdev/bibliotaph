using AppKit;
using Bibliotaph.Core;
using Bibliotaph.Mac.Binding;
using Bibliotaph.Mac.Platform;
using Bibliotaph.Presentation.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Bibliotaph.Mac.Views;

/// <summary>
/// Settings › Online libraries, as on Windows: each service with Open, the preview of a page read in the browser
/// with Import and Undo, books a page no longer lists, where store downloads go, and signing the browser out.
/// </summary>
public sealed partial class SettingsWindow
{
    NSScrollView OnlineTab()
    {
        var online = _services.GetRequiredService<MacOnlineLibraries>();
        var bindings = Load(online);

        var intro = Secondary("Bring in the books you own outside your folders: books you read on a service's own site, such as D&D Beyond and Demiplane, and PDFs you bought from stores, such as Humble Bundle and DriveThruRPG. Bibliotaph never holds your passwords and never browses on its own.");
        var readOnline = Services(online.Services.Where(s => s.ReadOnline), OnlineLibraryGroups.ReadOnline, OnlineLibraryGroups.ReadOnlineNote, bindings);
        var stores = Services(online.Services.Where(s => !s.ReadOnline), OnlineLibraryGroups.Stores, OnlineLibraryGroups.StoresNote, bindings);

        var problem = Secondary("");
        problem.TextColor = NSColor.SystemOrange;
        var undoMessage = Secondary("");
        var undo = Button("Undo", online.Undo.UndoCommand, bindings);
        var undoLine = Stack(vertical: false, spacing: 8, undoMessage, undo);
        bindings.Property(online, nameof(MacOnlineLibraries.Problem), () => Show(problem, online.Problem));
        bindings.Properties(online.Undo, [nameof(UndoSlot.Message), nameof(UndoSlot.HasUndo)], () =>
        {
            undoMessage.StringValue = online.Undo.Message ?? "";
            undoLine.Hidden = online.Undo.Message is null;
            undo.Hidden = !online.Undo.HasUndo;
        });

        var summary = Section("");
        var import = Button("", online.ImportCommand, bindings);
        var groups = Stack(vertical: true, spacing: 14);
        var preview = Stack(vertical: true, spacing: 10, Stack(vertical: false, spacing: 12, summary, import), groups);
        var rowBindings = new Bindings();
        bindings.Add(() => rowBindings.Dispose());
        void ShowGroups()
        {
            rowBindings.Dispose();
            rowBindings = new Bindings();
            foreach (var view in groups.ArrangedSubviews) view.RemoveFromSuperview();
            foreach (var group in online.Groups)
            {
                var rows = Stack(vertical: true, spacing: 6, Section(group.Heading), Secondary(group.Explanation));
                foreach (var item in group.Items)
                {
                    var tick = NSButton.CreateCheckbox(item.Title, () => { });
                    tick.Enabled = item.CanChoose;
                    tick.Activated += (_, _) => item.IsChosen = tick.State == NSCellStateValue.On;
                    rowBindings.Property(item, nameof(OnlineRowItem.IsChosen), () => tick.State = item.IsChosen ? NSCellStateValue.On : NSCellStateValue.Off);
                    var detail = Secondary(item.Detail);
                    detail.Font = NSFont.SystemFontOfSize(11)!;
                    rows.AddArrangedSubview(Stack(vertical: true, spacing: 1, tick, detail));
                }
                groups.AddArrangedSubview(rows);
            }
        }
        bindings.Collection(online.Groups, _ => ShowGroups());
        bindings.Properties(online, [nameof(MacOnlineLibraries.HasPlan), nameof(MacOnlineLibraries.Summary), nameof(MacOnlineLibraries.ImportLabel)], () =>
        {
            preview.Hidden = !online.HasPlan;
            summary.StringValue = online.Summary;
            import.Title = online.ImportLabel;
        });

        var missingList = Stack(vertical: true, spacing: 6);
        var missing = Stack(vertical: true, spacing: 6, Section("No longer in this library"),
            Secondary("Books imported before that this page doesn't list. Nothing is removed unless you remove it."), missingList);
        void ShowMissing()
        {
            foreach (var view in missingList.ArrangedSubviews) view.RemoveFromSuperview();
            foreach (var book in online.Missing)
            {
                var remove = NSButton.CreateButton("Remove from library", () => book.RemoveCommand.Execute(null));
                remove.ControlSize = NSControlSize.Small;
                missingList.AddArrangedSubview(Stack(vertical: false, spacing: 8, NSTextField.CreateLabel(book.Title), remove));
            }
            missing.Hidden = !online.HasMissing;
        }
        bindings.Collection(online.Missing, _ => ShowMissing());
        bindings.Property(online, nameof(MacOnlineLibraries.HasMissing), ShowMissing);

        var downloadsLine = Secondary("");
        var downloadsProblem = Secondary("");
        downloadsProblem.TextColor = NSColor.SystemOrange;
        var choose = Button("Choose folder…", online.ChooseDownloadsFolderCommand, bindings);
        var stop = Button("Stop using it", online.ClearDownloadsFolderCommand, bindings);
        bindings.Properties(online, [nameof(MacOnlineLibraries.DownloadsLine), nameof(MacOnlineLibraries.DownloadsProblem), nameof(MacOnlineLibraries.HasDownloadsFolder)], () =>
        {
            downloadsLine.StringValue = online.DownloadsLine;
            Show(downloadsProblem, online.DownloadsProblem);
            stop.Hidden = !online.HasDownloadsFolder;
        });
        var downloads = Stack(vertical: true, spacing: 6, Section("Downloads"),
            Secondary("When you press a store's own Download button in Bibliotaph's browser, the file goes here, a library folder or one inside it, and joins your library. Nothing downloads unless you press Download."),
            downloadsLine, downloadsProblem, Stack(vertical: false, spacing: 8, choose, stop));

        var browser = Stack(vertical: true, spacing: 6, Section("Bibliotaph's browser"),
            Secondary("It keeps you signed in between imports, apart from Safari. Signing out forgets every sign-in, cookie and page it kept."),
            Button("Sign out of all services", online.SignOutCommand, bindings));

        // What a page read shows first, as the window opens at the top when the browser reads one.
        return Scrolling(Stack(vertical: true, spacing: 18, intro, problem, undoLine, preview, missing, readOnline, stores, downloads, browser));
    }

    /// <summary>A group of services, each with what opening it does and its Open button.</summary>
    static NSStackView Services(IEnumerable<MacOnlineService> services, string heading, string note, Bindings bindings)
    {
        var group = Stack(vertical: true, spacing: 8, Section(heading), Secondary(note));
        foreach (var service in services)
        {
            var name = NSTextField.CreateLabel(service.Name);
            name.Font = NSFont.SystemFontOfSize(13, NSFontWeight.Medium)!;
            var explanation = Secondary(service.Explanation);
            explanation.Font = NSFont.SystemFontOfSize(11)!;
            explanation.PreferredMaxLayoutWidth = 420;
            var text = Stack(vertical: true, spacing: 2, name, explanation);
            text.WidthAnchor.ConstraintEqualTo(420).Active = true;
            group.AddArrangedSubview(Stack(vertical: false, spacing: 12, text, Button(service.OpenLabel, service.OpenCommand, bindings)));
        }
        return group;
    }
}
