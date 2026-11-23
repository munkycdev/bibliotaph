using AppKit;
using Bibliotaph.Core;
using Bibliotaph.Mac.Binding;
using Bibliotaph.Mac.Platform;
using Bibliotaph.Presentation.Services;
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
        var form = new Form();

        // What a page read shows first, as the window opens at its top when the browser reads one.
        var result = form.Section();
        var undo = Button("Undo", online.Undo.UndoCommand, bindings);
        var resultRow = result.Row("", undo);
        result.Footer.TextColor = NSColor.SystemOrange;
        void ShowResult()
        {
            result.Note(online.Problem);
            resultRow.Label.StringValue = online.Undo.Message ?? "";
            resultRow.Hidden = online.Undo.Message is null;
            undo.Hidden = !online.Undo.HasUndo;
            // Nothing to say takes no room at the top.
            result.View.Hidden = online.Problem is null && online.Undo.Message is null;
        }
        bindings.Property(online, nameof(MacOnlineLibraries.Problem), ShowResult);
        bindings.Properties(online.Undo, [nameof(UndoSlot.Message), nameof(UndoSlot.HasUndo)], ShowResult);

        var preview = new NSStackView { Orientation = NSUserInterfaceLayoutOrientation.Vertical, Alignment = NSLayoutAttribute.Leading, Spacing = 18 };
        form.Add(preview);
        var import = Button("", online.ImportCommand, bindings);
        import.KeyEquivalent = "\r";
        var importButtons = form.Buttons(import);
        var rowBindings = new Bindings();
        bindings.Add(() => rowBindings.Dispose());
        void ShowGroups()
        {
            rowBindings.Dispose();
            rowBindings = new Bindings();
            foreach (var view in preview.ArrangedSubviews) view.RemoveFromSuperview();
            var summary = NSTextField.CreateWrappingLabel(online.Summary);
            summary.Font = NSFont.SystemFontOfSize(15, NSFontWeight.Semibold)!;
            preview.AddArrangedSubview(summary);
            foreach (var group in online.Groups)
            {
                var section = new FormSection(group.Heading, group.Explanation);
                foreach (var item in group.Items)
                {
                    var tick = FormText.Switch(on => item.IsChosen = on);
                    tick.Enabled = item.CanChoose;
                    rowBindings.Property(item, nameof(OnlineRowItem.IsChosen), () => FormText.Set(tick, item.IsChosen));
                    section.Row(item.Title, item.Detail, tick);
                }
                preview.AddArrangedSubview(section.View);
                section.View.WidthAnchor.ConstraintEqualTo(preview.WidthAnchor).Active = true;
            }
        }
        bindings.Collection(online.Groups, _ => ShowGroups());
        bindings.Properties(online, [nameof(MacOnlineLibraries.HasPlan), nameof(MacOnlineLibraries.Summary), nameof(MacOnlineLibraries.ImportLabel)], () =>
        {
            preview.Hidden = importButtons.Hidden = !online.HasPlan;
            import.Title = online.ImportLabel;
            ShowGroups();
        });

        var missing = form.Section("No longer in this library", "Books imported before that this page doesn't list. Nothing is removed unless you remove it.");
        void ShowMissing()
        {
            missing.Clear();
            foreach (var book in online.Missing)
                missing.Row(book.Title, NSButton.CreateButton("Remove from Library", () => book.RemoveCommand.Execute(null)));
            missing.View.Hidden = !online.HasMissing;
        }
        bindings.Collection(online.Missing, _ => ShowMissing());
        bindings.Property(online, nameof(MacOnlineLibraries.HasMissing), ShowMissing);

        Services(form, online.Services.Where(s => s.ReadOnline), OnlineLibraryGroups.ReadOnline, OnlineLibraryGroups.ReadOnlineNote, bindings);
        Services(form, online.Services.Where(s => !s.ReadOnline), OnlineLibraryGroups.Stores, OnlineLibraryGroups.StoresNote, bindings);

        var downloads = form.Section("Downloads",
            "When you press a store's own Download button in Bibliotaph's browser, the file goes here, a library folder or one inside it, and joins your library. Nothing downloads unless you press Download.");
        var stop = Button("Stop Using It", online.ClearDownloadsFolderCommand, bindings);
        var folder = downloads.Row("Downloads folder", stop, Button("Choose…", online.ChooseDownloadsFolderCommand, bindings));
        bindings.Properties(online, [nameof(MacOnlineLibraries.DownloadsLine), nameof(MacOnlineLibraries.DownloadsProblem), nameof(MacOnlineLibraries.HasDownloadsFolder)], () =>
        {
            folder.ShowDetail(online.DownloadsProblem ?? online.DownloadsLine);
            folder.Detail.TextColor = online.DownloadsProblem is null ? NSColor.SecondaryLabel : NSColor.SystemOrange;
            stop.Hidden = !online.HasDownloadsFolder;
        });

        CatalogueKeys(form, _services.GetRequiredService<CatalogueKeysViewModel>(), bindings);

        // As Windows has it: books owned online open in Bibliotaph's own reader, or in the everyday browser.
        var readers = _services.GetRequiredService<OnlineReaders>();
        var owned = form.Section("Books you own online");
        var sites = _services.GetRequiredService<MacSiteIcons>();
        var siteIcons = FormText.Switch(on => sites.SetOnAsync(on).FireAndForget());
        owned.Row("Show site icons",
            "A book you own online shows its site's icon, such as D&D Beyond's, in the corner of its cover. Bibliotaph fetches each site's icon once and keeps it. Off, a plain cloud shows and nothing is fetched.",
            siteIcons);
        LoadSitesAsync().FireAndForget();
        async Task LoadSitesAsync()
        {
            var on = await sites.IsOnAsync();
            Bindings.OnMain(() => FormText.Set(siteIcons, on));
        }
        var inApp = FormText.Switch(on => readers.SetOpensInAppAsync(on).FireAndForget());
        owned.Row("Read them in Bibliotaph",
            "Open shows a book you own on D&D Beyond or Demiplane on its own page, in a reader like the PDF reader, signed in through Bibliotaph's browser. Off, it opens in your everyday browser.",
            inApp);
        LoadInAppAsync().FireAndForget();
        async Task LoadInAppAsync()
        {
            var on = await readers.OpensInAppAsync();
            Bindings.OnMain(() => FormText.Set(inApp, on));
        }

        var browser = form.Section("Bibliotaph's browser",
            "It keeps you signed in between imports, apart from Safari. Signing out forgets every sign-in, cookie and page it kept.");
        browser.Row("Sign-ins", Button("Sign Out of All Services", online.SignOutCommand, bindings));
        return Pane(form);
    }

    /// <summary>
    /// The user's own RPGGeek token and Google Books key, for looking books up without bibliotaph.online: a secure field
    /// and Save each, or Forget once one is saved, with where to get one.
    /// </summary>
    static void CatalogueKeys(Form form, CatalogueKeysViewModel keys, Bindings bindings)
    {
        var section = form.Section("Book catalogues",
            "While this Mac isn't connected to bibliotaph.online, it looks books up itself: AdventureLookup and Open Library always, and RPGGeek and Google Books with a token or key of your own. They're kept in your Keychain. Only ISBNs, product codes and titles are sent. RPGGeek's data is powered by BGG.");
        Key(section, "RPGGeek token", keys.RpgGeekState, CatalogueKeysViewModel.RpgGeekRegistration, "Register an app with BoardGameGeek for one",
            () => keys.NewRpgGeekToken, v => keys.NewRpgGeekToken = v, keys.SaveRpgGeekTokenCommand, keys.ForgetRpgGeekTokenCommand,
            () => keys.HasRpgGeekToken, nameof(CatalogueKeysViewModel.RpgGeekState), () => keys.RpgGeekState, keys, bindings);
        Key(section, "Google Books API key", keys.GoogleBooksState, CatalogueKeysViewModel.GoogleBooksConsole, "Turn on the Books API in your Google Cloud project for one",
            () => keys.NewGoogleBooksKey, v => keys.NewGoogleBooksKey = v, keys.SaveGoogleBooksKeyCommand, keys.ForgetGoogleBooksKeyCommand,
            () => keys.HasGoogleBooksKey, nameof(CatalogueKeysViewModel.GoogleBooksState), () => keys.GoogleBooksState, keys, bindings);
        bindings.Property(keys, nameof(CatalogueKeysViewModel.Problem), () =>
        {
            if (keys.Problem is { } problem) section.Note(problem);
        });
    }

    static void Key(FormSection section, string label, string state, Uri source, string sourceText, Func<string> get, Action<string> set,
        System.Windows.Input.ICommand save, System.Windows.Input.ICommand forget, Func<bool> saved, string stateProperty, Func<string> currentState,
        CatalogueKeysViewModel keys, Bindings bindings)
    {
        var field = new NSSecureTextField { PlaceholderString = "Paste it here" };
        field.WidthAnchor.ConstraintEqualTo(200).Active = true;
        field.Changed += (_, _) => set(field.StringValue);
        field.Activated += (_, _) =>
        {
            if (save.CanExecute(null)) save.Execute(null);
        };
        var saveButton = Button("Save", save, bindings);
        var forgetButton = Button("Forget", forget, bindings);
        var link = NSButton.CreateButton(sourceText + "…", () => NSWorkspace.SharedWorkspace.OpenUrl(new Foundation.NSUrl(source.AbsoluteUri)));
        // A link, as the Mac's settings put one under a row: plain text in the accent, from the leading edge.
        link.Bordered = false;
        link.ControlSize = NSControlSize.Small;
        link.Alignment = NSTextAlignment.Left;
        link.AttributedTitle = new Foundation.NSAttributedString(sourceText + "…", new AppKit.NSStringAttributes
        {
            ForegroundColor = NSColor.ControlAccent,
            Font = NSFont.SystemFontOfSize(11)!,
        });
        var row = section.Row(label, state, field, saveButton, forgetButton);
        bindings.Property(keys, stateProperty, () =>
        {
            row.ShowDetail(currentState());
            field.Hidden = saveButton.Hidden = saved();
            forgetButton.Hidden = !saved();
            if (field.StringValue != get()) field.StringValue = get();
        });
        section.Wide(link);
    }

    /// <summary>A group of services, each with what opening it does and its Open button.</summary>
    static void Services(Form form, IEnumerable<MacOnlineService> services, string heading, string note, Bindings bindings)
    {
        var group = form.Section(heading, note);
        foreach (var service in services) group.Row(service.Name, service.Explanation, Button("Open…", service.OpenCommand, bindings));
    }
}
