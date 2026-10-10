using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Bibliotaph.App.ViewModels;
using Bibliotaph.App.Views;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Core.Search;
using Bibliotaph.Index;
using Bibliotaph.Processing;
using Microsoft.Extensions.DependencyInjection;

namespace Bibliotaph.App.Services;

/// <summary>Slice 4i: passwords, DRM and forgetting a book's text, on two hand-made PDFs (<see cref="SmokePdfs"/>).</summary>
static partial class SmokeTest
{
    const string LockedTitle = "Smoke Locked Book";
    const string ProtectedTitle = "Smoke Protected Book";

    /// <summary>
    /// The password dialog, shown without waiting on it: "Make its text searchable" is ticked to start with, and ticking
    /// Remember ticks and locks it, since a remembered password lets indexing read the book anyway.
    /// </summary>
    static async Task ShowPasswordDialogAsync(Window window)
    {
        var dialog = new PasswordDialog("Smoke Locked Book", retry: false) { Owner = window };
        try
        {
            dialog.Show();
            await Settle(window);
            await AuditAsync(dialog, "the password dialog");
            if (!dialog.MakeSearchable || dialog.RememberPassword) throw new InvalidOperationException("The password dialog doesn't start with only Make its text searchable ticked.");
            dialog.Searchable.IsChecked = false;
            if (dialog.MakeSearchable) throw new InvalidOperationException("Unticking Make its text searchable didn't take.");
            dialog.Remember.IsChecked = true;
            await Settle(window);
            if (!dialog.MakeSearchable || dialog.Searchable.IsEnabled)
                throw new InvalidOperationException("Ticking Remember didn't tick and lock Make its text searchable.");
            dialog.Remember.IsChecked = false;
            await Settle(window);
            if (!dialog.Searchable.IsEnabled) throw new InvalidOperationException("Unticking Remember left Make its text searchable locked.");
        }
        finally
        {
            dialog.Close();
            await Settle(window);
        }
    }

    /// <summary>
    /// A ZIP with a locked PDF and one under a protection scheme Bibliotaph can't open, read by the real pipeline (indexing
    /// runs from Reprocess on). Their cards, details and Needs review rows say which is which and offer what helps: the
    /// protected one opens in another app (the smoke test's launcher only records it), and the locked one, unlocked in
    /// the reader without Remember, has Copy off as its file asks and becomes searchable for this sitting.
    /// </summary>
    static async Task UseProtectedBooksAsync(IServiceProvider services, Window window)
    {
        var folder = System.IO.Directory.CreateTempSubdirectory("bibliotaph-smoke-protected-").FullName;
        var zipPath = System.IO.Path.Combine(folder, "Smoke Protected.zip");
        using (var zip = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create))
        {
            await using (var member = zip.CreateEntry($"Smoke Protected/{LockedTitle}.pdf").Open()) await member.WriteAsync(SmokePdfs.Locked());
            await using (var member = zip.CreateEntry($"Smoke Protected/{ProtectedTitle}.pdf").Open()) await member.WriteAsync(SmokePdfs.Protected());
        }
        var root = await services.GetRequiredService<SourceRootStore>().AddAsync(folder);
        services.GetRequiredService<IndexingService>().RequestScan(root.Id);

        var queries = services.GetRequiredService<LibraryQueries>();
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 60;
        IReadOnlyList<LibraryEntry> entries;
        while ((entries = await Task.Run(() => queries.ListAsync(new LibraryFilter()))) is var all
            && !(all.Any(e => e is { Title: LockedTitle, TextAccess: TextAccess.Locked }) && all.Any(e => e is { Title: ProtectedTitle, TextAccess: TextAccess.Protected })))
        {
            if (Stopwatch.GetTimestamp() > deadline)
                throw new InvalidOperationException($"The locked and protected PDFs weren't read as such: {string.Join(", ", all.Where(e => e.Title.StartsWith("Smoke ", StringComparison.Ordinal)).Select(e => $"{e.Title} {e.TextAccess}"))}.");
            await Settle(window);
            await Task.Delay(200);
        }
        var locked = entries.First(e => e.Title == LockedTitle);
        var shielded = entries.First(e => e.Title == ProtectedTitle);
        if (locked.Searchable || shielded.Searchable) throw new InvalidOperationException("A locked or protected PDF is called searchable.");

        // The cards: a mark and what's wrong, instead of "still being read" for ever.
        var shell = services.GetRequiredService<ShellViewModel>();
        var navigation = services.GetRequiredService<INavigationService>();
        navigation.NavigateTo(Route.Library);
        var library = shell.CurrentPage as LibraryViewModel ?? throw new InvalidOperationException("The Library didn't open.");
        await library.RefreshAsync();
        await WaitUntilAsync(window, () => library.Items.Any(i => i is { Title: LockedTitle, Meta: "PDF · Locked: open it to unlock", TextMark: "Locked" })
            && library.Items.Any(i => i is { Title: ProtectedTitle, Meta: "PDF · Can't be read here", TextMark: "Protected", CanOpenElsewhere: true }),
            () => $"The cards read {string.Join("; ", library.Items.Where(i => i.Title.StartsWith("Smoke ", StringComparison.Ordinal)).Select(i => $"{i.Title}: {i.Meta} [{i.TextMark}]"))}.");

        // The details: why, in words, and Open in another app for the protected one.
        await library.OpenDetailsCommand.ExecuteAsync(library.Items.First(i => i.DocumentId == locked.DocumentId));
        await WaitUntilAsync(window, () => library.Inspector?.Facts.Contains(new InspectorFact("Search", "Locked: open it to unlock, and its text becomes searchable")) == true
            && library.Inspector.Facts.Any(f => f.Label == "Password") && !Shown(window, "OpenElsewhere"),
            () => $"The locked PDF's details say {string.Join("; ", library.Inspector?.Facts.Select(f => $"{f.Label}: {f.Value}") ?? [])}.");
        await library.OpenDetailsCommand.ExecuteAsync(library.Items.First(i => i.DocumentId == shielded.DocumentId));
        await WaitUntilAsync(window, () => library.Inspector?.Facts.Any(f => f.Label == "Protection") == true && Shown(window, "OpenElsewhere"),
            () => $"The protected PDF's details say {string.Join("; ", library.Inspector?.Facts.Select(f => $"{f.Label}: {f.Value}") ?? [])}.");
        var launcher = (SmokeShellLauncher)services.GetRequiredService<IShellLauncher>();
        var opened = launcher.Opened.Count;
        Click(Descendants<Button>(window).FirstOrDefault(b => b.Name == "OpenElsewhere" && b.IsVisible), "Open in another app");
        await WaitUntilAsync(window, () => launcher.Opened.Count == opened + 1 && launcher.Opened[^1] == zipPath,
            () => $"Open in another app opened {string.Join(", ", launcher.Opened.Skip(opened))}, not the ZIP the PDF is in.");
        library.CloseDetailsCommand.Execute(null);
        await Settle(window);

        // Needs review: no Try again for either; what helps instead.
        navigation.NavigateTo(Route.NeedsReview);
        var review = shell.CurrentPage as NeedsReviewViewModel ?? throw new InvalidOperationException("Needs review didn't open.");
        review.IsFilesTab = true;
        await WaitUntilAsync(window, () => review.Files.Any(f => f is { Title: LockedTitle, CanUnlock: true, CanRetry: false })
            && review.Files.Any(f => f is { Title: ProtectedTitle, CanOpenElsewhere: true, CanRetry: false })
            && Descendants<Button>(window).Any(b => b.Command == review.OpenElsewhereCommand && b.CommandParameter is AttentionRow { Title: ProtectedTitle } && b.IsVisible),
            () => $"Files needing attention list {string.Join("; ", review.Files.Select(f => $"{f.Title} (retry {f.CanRetry}, unlock {f.CanUnlock}, elsewhere {f.CanOpenElsewhere})"))}.");
        if (Descendants<Button>(window).Any(b => b.Command == review.RetryCommand && b.CommandParameter is AttentionRow { Title: LockedTitle or ProtectedTitle } && b.IsVisible))
            throw new InvalidOperationException("Needs review offers Try again for a locked or protected PDF.");
        navigation.GoBack();
        await Settle(window);

        // The reader: the protected one's problem screen offers the other app, not Try again.
        var readers = services.GetRequiredService<ReaderWindows>();
        readers.OpenInMainWindow(new ViewerRequest(shielded.DocumentId, ProtectedTitle, 0));
        var viewer = shell.CurrentPage as ViewerViewModel ?? throw new InvalidOperationException("The protected PDF didn't open the viewer.");
        await WaitUntilAsync(window, () => viewer is { Mode: ViewerMode.Problem, EmptyTitle: "Can't be read here.", EmptyActionText: "Open in another app" },
            () => $"The protected PDF shows {viewer.Mode}: {viewer.EmptyTitle} [{viewer.EmptyActionText}].");
        opened = launcher.Opened.Count;
        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)viewer.EmptyCommand!).ExecuteAsync(null);
        if (launcher.Opened.Count != opened + 1 || launcher.Opened[^1] != zipPath) throw new InvalidOperationException("The reader's Open in another app didn't open the file's ZIP.");
        navigation.GoBack();
        await Settle(window);

        // The locked one, unlocked without Remember: Copy stays off as the file asks, and its text becomes searchable.
        var prompt = (SmokePasswordPrompt)services.GetRequiredService<IPasswordPrompt>();
        prompt.Answer = new EnteredPassword(SmokePdfs.Password, Remember: false, Searchable: true);
        try
        {
            readers.OpenInMainWindow(new ViewerRequest(locked.DocumentId, LockedTitle, 0));
            viewer = shell.CurrentPage as ViewerViewModel ?? throw new InvalidOperationException("The locked PDF didn't open the viewer.");
            await WaitUntilAsync(window, () => viewer.IsPdf || viewer.Mode == ViewerMode.Problem, () => $"The locked PDF didn't open (still {viewer.Mode}).");
            if (!viewer.IsPdf) throw new InvalidOperationException($"The locked PDF didn't open with its password: {viewer.EmptyTitle} {viewer.EmptyMessage}");
            if (viewer.CanCopy) throw new InvalidOperationException("Copy is on in the reader for a PDF that forbids copying.");
        }
        finally
        {
            prompt.Answer = null;
        }
        if (services.GetRequiredService<IPasswordVault>().Find((await services.GetRequiredService<LibraryStore>().GetSourceAsync(locked.DocumentId))!.ContentHash) is not null)
            throw new InvalidOperationException("A password entered without Remember was remembered.");
        navigation.GoBack();
        await Settle(window);
        await WaitFoundAsync(services, window, locked.DocumentId, found: true, "unlocked without Remember");
    }

    /// <summary>
    /// Forget its text, from the card's menu command, on the book unlocked before: its card says so and a search no longer
    /// finds it; Read it again reads it once more with the password lent for this sitting.
    /// </summary>
    static async Task ForgetAndReadAgainAsync(IServiceProvider services, Window window)
    {
        var shell = services.GetRequiredService<ShellViewModel>();
        services.GetRequiredService<INavigationService>().NavigateTo(Route.Library);
        var library = shell.CurrentPage as LibraryViewModel ?? throw new InvalidOperationException("The Library didn't open.");
        await library.RefreshAsync();
        await WaitUntilAsync(window, () => library.Items.Any(i => i is { Title: LockedTitle, TextAccess: TextAccess.Readable, CanForgetText: true }),
            () => "The unlocked PDF's card doesn't offer Forget its text.");
        var item = library.Items.First(i => i.Title == LockedTitle);
        if (!library.ForgetTextCommand.CanExecute(item) || library.ReadTextAgainCommand.CanExecute(item))
            throw new InvalidOperationException("The card's menu offers Read it again before its text was forgotten.");

        await library.ForgetTextCommand.ExecuteAsync(item);
        // Its labels (from its folder's name) lead the line; how its text stands ends it.
        await WaitUntilAsync(window, () => library.Items.Any(i => i is { Title: LockedTitle, TextAccess: TextAccess.Forgotten, CanReadAgain: true }
            && i.Meta.EndsWith(" · Text forgotten", StringComparison.Ordinal)),
            () => $"After Forget its text the card reads {library.Items.FirstOrDefault(i => i.Title == LockedTitle)?.Meta}.");
        await WaitFoundAsync(services, window, item.DocumentId, found: false, "forgotten");

        await library.ReadTextAgainCommand.ExecuteAsync(library.Items.First(i => i.Title == LockedTitle));
        await WaitFoundAsync(services, window, item.DocumentId, found: true, "read again");
    }

    /// <summary>
    /// Settings > Passwords lists the book whose password was remembered, by its title, and Forget there forgets it, in
    /// the vault (the smoke test's own, in memory) and for this sitting.
    /// </summary>
    static async Task ForgetPasswordAsync(IServiceProvider services, Window window)
    {
        var locked = (await Task.Run(() => services.GetRequiredService<LibraryQueries>().ListAsync(new LibraryFilter()))).First(e => e.Title == LockedTitle);
        var hash = (await services.GetRequiredService<LibraryStore>().GetSourceAsync(locked.DocumentId))!.ContentHash;
        var vault = services.GetRequiredService<IPasswordVault>();
        var unlocked = services.GetRequiredService<UnlockedPasswords>();
        if (!vault.Remember(hash, SmokePdfs.Password)) throw new InvalidOperationException("The smoke vault didn't remember a password.");

        var page = (PasswordsSectionViewModel)(await OpenSettingsAsync(services, window, SettingsSection.Passwords)).Selected;
        PasswordRow? row = null;
        await WaitUntilAsync(window, () => (row = page.Rows.FirstOrDefault(r => r.ContentHash == hash)) is { Title: LockedTitle }
            && Descendants<Button>(window).Any(b => b.Command == page.ForgetCommand && ReferenceEquals(b.CommandParameter, row) && b.IsVisible),
            () => $"Settings > Passwords lists {string.Join(", ", page.Rows.Select(r => r.Title))}.");
        await AuditAsync(window, "Settings > Passwords with a password");
        Click(Descendants<Button>(window).First(b => b.Command == page.ForgetCommand && ReferenceEquals(b.CommandParameter, row) && b.IsVisible),
            $"Forget the password of {LockedTitle}");
        await WaitUntilAsync(window, () => page.Rows.Count == 0 && Descendants<TextBlock>(window).Any(t => t is { Text: "No passwords are remembered.", IsVisible: true }),
            () => $"Forget left {page.Rows.Count} passwords listed.");
        if (vault.Find(hash) is not null || unlocked.Find(hash) is not null) throw new InvalidOperationException("Forget in Settings > Passwords didn't forget the password.");
        services.GetRequiredService<INavigationService>().GoBack();
        await Settle(window);
    }

    /// <summary>Waits until a search for the locked book's words finds it, or stops finding it.</summary>
    static async Task WaitFoundAsync(IServiceProvider services, Window window, long documentId, bool found, string when)
    {
        var queries = services.GetRequiredService<LibraryQueries>();
        var libraryStore = services.GetRequiredService<LibraryStore>();
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 60;
        while (true)
        {
            var filter = new LibraryFilter(await libraryStore.GetVisibleEntryIdsAsync());
            var hits = await Task.Run(() => queries.SearchPagesAsync(SearchPlan.From(SearchQuery.Parse("lantern gate")), filter));
            if (hits.Entries.Any(e => e.Entry.DocumentId == documentId) == found) return;
            if (Stopwatch.GetTimestamp() > deadline)
                throw new InvalidOperationException($"The locked PDF, {when}, is {(found ? "not " : "")}found by the words on its page.");
            await Settle(window);
            await Task.Delay(200);
        }
    }
}
