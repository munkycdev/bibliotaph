using AppKit;
using Bibliotaph.Mac.Binding;
using Bibliotaph.Presentation.Services;
using Bibliotaph.Presentation.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Bibliotaph.Mac.Views;

/// <summary>Settings' Processing, Backup and Passwords tabs, each the Windows section's view model.</summary>
public sealed partial class SettingsWindow
{
    /// <summary>Each step of reading the library with its bar and Run again, what is happening now, and pausing.</summary>
    NSScrollView ProcessingTab()
    {
        var processing = _services.GetRequiredService<ProcessingSectionViewModel>();
        var bindings = Load(processing);
        var activity = processing.Activity;

        var intro = Secondary("Only while Bibliotaph is open. Closing the app stops it; reopening resumes it.");
        var noFolders = Secondary("Nothing to process until you add a folder.");
        var addFolder = Button("Add a folder in Library", processing.OpenLibraryCommand, bindings);
        var summary = Heading("");
        var timeLeft = Secondary("");
        bindings.Properties(activity, [nameof(LibraryActivity.Summary), nameof(LibraryActivity.TimeLeftText)], () =>
        {
            summary.StringValue = activity.Summary;
            timeLeft.StringValue = activity.TimeLeftText;
            timeLeft.Hidden = activity.TimeLeftText.Length == 0;
        });

        var phases = Stack(vertical: true, spacing: 10);
        foreach (var phase in activity.Phases) phases.AddArrangedSubview(Phase(processing, phase, bindings));
        var runAgainNote = Secondary("");
        bindings.Property(processing, nameof(ProcessingSectionViewModel.RunAgainNote), () =>
        {
            runAgainNote.StringValue = processing.RunAgainNote;
            runAgainNote.Hidden = processing.RunAgainNote.Length == 0;
        });

        var now = Secondary("");
        var notes = Secondary("");
        var waiting = Secondary("");
        waiting.TextColor = NSColor.SystemOrange;
        bindings.Properties(activity, [nameof(LibraryActivity.Now), nameof(LibraryActivity.Notes), nameof(LibraryActivity.AiWaiting)], () =>
        {
            now.StringValue = string.Join("\n", activity.Now);
            now.Hidden = activity.Now.Count == 0;
            notes.StringValue = activity.Notes;
            notes.Hidden = activity.Notes.Length == 0;
            waiting.StringValue = activity.IsAiWaiting ? $"Cataloguing with AI is waiting: {activity.AiWaiting}" : "";
            waiting.Hidden = !activity.IsAiWaiting;
        });
        var attention = Button("Files needing attention…", processing.OpenFilesNeedingAttentionCommand, bindings);
        var aiReading = Button("See what the AI is reading", processing.OpenAiReadingCommand, bindings);

        var pauseIndex = Button("", processing.ToggleIndexingCommand, bindings);
        var pauseOcr = Button("", processing.ToggleOcrCommand, bindings);
        var pauseAi = Button("", processing.ToggleAiCommand, bindings);
        bindings.Properties(activity, [nameof(LibraryActivity.IndexPaused), nameof(LibraryActivity.OcrPaused), nameof(LibraryActivity.AiPaused),
            nameof(LibraryActivity.ShowIndexPause), nameof(LibraryActivity.ShowOcrPause), nameof(LibraryActivity.ShowAiPause)], () =>
        {
            pauseIndex.Title = activity.IndexPaused ? "Resume indexing" : "Pause indexing";
            pauseOcr.Title = activity.OcrPaused ? "Resume reading scanned pages" : "Pause reading scanned pages";
            pauseAi.Title = activity.AiPaused ? "Resume cataloguing with AI" : "Pause cataloguing with AI";
            pauseIndex.Hidden = !activity.ShowIndexPause;
            pauseOcr.Hidden = !activity.ShowOcrPause;
            pauseAi.Hidden = !activity.ShowAiPause;
        });

        var lookUp = Secondary("");
        bindings.Property(processing, nameof(ProcessingSectionViewModel.LookUpExplanation), () => lookUp.StringValue = processing.LookUpExplanation);

        var work = Stack(vertical: true, spacing: 12,
            Stack(vertical: true, spacing: 2, summary, timeLeft), phases, runAgainNote,
            Stack(vertical: true, spacing: 4, Section("Now"), now), notes, waiting,
            Stack(vertical: false, spacing: 8, attention, aiReading),
            Stack(vertical: false, spacing: 8, pauseIndex, pauseOcr, pauseAi),
            Stack(vertical: true, spacing: 4, Section("Looking up online"), lookUp, Secondary("Your files, notes and collections stay on this device.")));
        foreach (var view in work.ArrangedSubviews) view.WidthAnchor.ConstraintEqualTo(work.WidthAnchor).Active = true;
        var empty = Stack(vertical: true, spacing: 8, noFolders, addFolder);
        bindings.Property(processing, nameof(ProcessingSectionViewModel.HasFolders), () =>
        {
            work.Hidden = !processing.HasFolders;
            empty.Hidden = processing.HasFolders;
        });
        return Scrolling(Stack(vertical: true, spacing: 16, intro, empty, work));
    }

    /// <summary>A step's name and what it does, its bar, how far it has got, and Run again when it can be.</summary>
    static NSStackView Phase(ProcessingSectionViewModel processing, PhaseProgress phase, Bindings bindings)
    {
        var label = NSTextField.CreateLabel(phase.Label);
        label.Font = NSFont.SystemFontOfSize(13, NSFontWeight.Medium)!;
        var detail = Secondary(phase.Detail);
        detail.Font = NSFont.SystemFontOfSize(11)!;
        var bar = new NSProgressIndicator { Style = NSProgressIndicatorStyle.Bar, MinValue = 0, MaxValue = 100, ControlSize = NSControlSize.Small };
        bar.WidthAnchor.ConstraintEqualTo(180).Active = true;
        var status = NSTextField.CreateLabel("");
        status.Font = NSFont.SystemFontOfSize(11)!;
        status.TextColor = NSColor.SecondaryLabel;
        var runAgain = NSButton.CreateButton("Run again", () => processing.RunAgainCommand.Execute(phase));
        runAgain.ControlSize = NSControlSize.Small;
        bindings.Properties(phase, [nameof(PhaseProgress.Percent), nameof(PhaseProgress.IsIndeterminate), nameof(PhaseProgress.Status),
            nameof(PhaseProgress.State), nameof(PhaseProgress.RunAgain)], () =>
        {
            bar.Indeterminate = phase.IsIndeterminate;
            if (phase.IsIndeterminate && phase.State == PhaseState.Running) bar.StartAnimation(null);
            else bar.StopAnimation(null);
            bar.DoubleValue = phase.Percent;
            status.StringValue = phase.Status;
            runAgain.Hidden = phase.RunAgain is null;
            runAgain.ToolTip = phase.RunAgain?.Hint ?? "";
        });
        // A column of its own, so the bars line up.
        detail.PreferredMaxLayoutWidth = 240;
        var text = Stack(vertical: true, spacing: 1, label, detail);
        text.WidthAnchor.ConstraintEqualTo(240).Active = true;
        return Stack(vertical: false, spacing: 12, text, bar, status, runAgain);
    }

    /// <summary>Back up now, restore from a backup with the folders it names, and export the catalog.</summary>
    NSScrollView BackupTab()
    {
        var backup = _services.GetRequiredService<BackupSectionViewModel>();
        var bindings = Load(backup);

        var intro = Secondary("A backup holds your catalog: folders, corrections, collections, binders and notes. Your books aren't in it; they stay where they are.");
        var backUp = Button("Back up now…", backup.BackUpNowCommand, bindings);
        var openFolder = Button("Show backups in Finder", backup.OpenBackupsFolderCommand, bindings);
        var backupNote = Secondary("");
        var last = Secondary("");
        bindings.Properties(backup, [nameof(BackupSectionViewModel.BackupNote), nameof(BackupSectionViewModel.LastBackup)], () =>
        {
            backupNote.StringValue = backup.BackupNote ?? "";
            backupNote.Hidden = backup.BackupNote is null;
            last.StringValue = backup.LastBackup;
        });
        var includeIndex = NSButton.CreateCheckbox("Include the search index", () => { });
        includeIndex.Activated += (_, _) => backup.IncludeIndex = includeIndex.State == NSCellStateValue.On;
        bindings.Property(backup, nameof(BackupSectionViewModel.IncludeIndex), () => includeIndex.State = backup.IncludeIndex ? NSCellStateValue.On : NSCellStateValue.Off);
        var indexNote = Secondary("Backups get much larger, but a restored library is searchable straight away instead of after your books are read again.");

        var choose = Button("Choose a backup…", backup.ChooseBackupCommand, bindings);
        var restoreSummary = Secondary("");
        var restoreProblem = Secondary("");
        restoreProblem.TextColor = NSColor.SystemOrange;
        var folders = Stack(vertical: true, spacing: 6);
        var restore = Button("Restore and restart…", backup.RestoreCommand, bindings);
        var cancel = Button("Cancel", backup.CancelRestoreCommand, bindings);
        var preview = Stack(vertical: true, spacing: 8, folders,
            Secondary("Passwords for protected PDFs and AI keys aren't in backups. Bibliotaph asks for them again when it needs them."),
            Stack(vertical: false, spacing: 8, restore, cancel));
        var folderBindings = new Bindings();
        bindings.Add(() => folderBindings.Dispose());
        void ShowFolders()
        {
            folderBindings.Dispose();
            folderBindings = new Bindings();
            foreach (var view in folders.ArrangedSubviews) view.RemoveFromSuperview();
            foreach (var folder in backup.RestoreFolders) folders.AddArrangedSubview(RestoreFolder(backup, folder, folderBindings));
        }
        bindings.Collection(backup.RestoreFolders, _ => ShowFolders());
        bindings.Properties(backup, [nameof(BackupSectionViewModel.RestoreSummary), nameof(BackupSectionViewModel.RestoreProblem),
            nameof(BackupSectionViewModel.HasPreview)], () =>
        {
            restoreSummary.StringValue = backup.RestoreSummary ?? "";
            restoreSummary.Hidden = backup.RestoreSummary is null;
            restoreProblem.StringValue = backup.RestoreProblem ?? "";
            restoreProblem.Hidden = backup.RestoreProblem is null;
            preview.Hidden = !backup.HasPreview;
            ShowFolders();
        });

        var exportCsv = Button("Export CSV…", backup.ExportCsvCommand, bindings);
        var exportJson = Button("Export JSON…", backup.ExportJsonCommand, bindings);
        var exportNote = Secondary("");
        bindings.Property(backup, nameof(BackupSectionViewModel.ExportNote), () =>
        {
            exportNote.StringValue = backup.ExportNote ?? "";
            exportNote.Hidden = backup.ExportNote is null;
        });

        return Scrolling(Stack(vertical: true, spacing: 18,
            Stack(vertical: true, spacing: 8, Section("Backup"), intro, Stack(vertical: false, spacing: 8, backUp, openFolder), backupNote, last,
                includeIndex, indexNote),
            Stack(vertical: true, spacing: 8, Section("Restore"),
                Secondary("Puts your catalog back as it was when a backup was made, on this Mac or a new one. Your current catalog is backed up first."),
                choose, restoreSummary, restoreProblem, preview),
            Stack(vertical: true, spacing: 8, Section("Export"),
                Secondary("CSV has a row per book: title, system, type, levels, publisher, tags, collections and where its file is. JSON has everything in the catalog, for other programs."),
                Stack(vertical: false, spacing: 8, exportCsv, exportJson), exportNote)));
    }

    /// <summary>A folder the backup names: found here, or pointed to its new place, or left offline.</summary>
    static NSStackView RestoreFolder(BackupSectionViewModel backup, RestoreFolderItem folder, Bindings bindings)
    {
        var path = NSTextField.CreateLabel(folder.Path);
        path.LineBreakMode = NSLineBreakMode.TruncatingMiddle;
        path.Font = NSFont.SystemFontOfSize(12, NSFontWeight.Medium)!;
        var status = Secondary("");
        status.Font = NSFont.SystemFontOfSize(11)!;
        var point = NSButton.CreateButton("Point to its new place…", () => backup.PointRestoredFolderCommand.Execute(folder));
        point.ControlSize = NSControlSize.Small;
        var leave = NSButton.CreateButton("Leave it offline", () => backup.LeaveOfflineCommand.Execute(folder));
        leave.ControlSize = NSControlSize.Small;
        var problem = Secondary("");
        problem.TextColor = NSColor.SystemOrange;
        bindings.Properties(folder, [nameof(RestoreFolderItem.NewPlace), nameof(RestoreFolderItem.Problem)], () =>
        {
            status.StringValue = $"{folder.Files} · {folder.Status}";
            point.Hidden = folder.Found;
            leave.Hidden = !folder.IsPointed;
            problem.StringValue = folder.Problem;
            problem.Hidden = !folder.HasProblem;
        });
        return Stack(vertical: true, spacing: 3, path, status, Stack(vertical: false, spacing: 8, point, leave), problem);
    }

    /// <summary>The books whose PDF password is remembered in the Keychain, each with Forget, and Forget all.</summary>
    NSScrollView PasswordsTab()
    {
        var passwords = _services.GetRequiredService<PasswordsSectionViewModel>();
        var bindings = Load(passwords);
        var intro = Secondary("PDF passwords you chose to remember, kept in your Keychain on this Mac, never in Bibliotaph's own files. Bibliotaph opens these books with them, and reads them for search. Forget one and you'll be asked for it the next time you open the book; text already read stays searchable.");
        var none = Secondary("No passwords are remembered.");
        var rows = Stack(vertical: true, spacing: 6);
        var forgetAll = Button("Forget all", passwords.ForgetAllCommand, bindings);
        void Show()
        {
            foreach (var view in rows.ArrangedSubviews) view.RemoveFromSuperview();
            foreach (var row in passwords.Rows)
            {
                var title = NSTextField.CreateLabel(row.Title);
                title.LineBreakMode = NSLineBreakMode.TruncatingTail;
                title.SetContentHuggingPriorityForOrientation(200, NSLayoutConstraintOrientation.Horizontal);
                var forget = NSButton.CreateButton("Forget", () => passwords.ForgetCommand.Execute(row));
                forget.ControlSize = NSControlSize.Small;
                var line = Stack(vertical: false, spacing: 8, title, forget);
                rows.AddArrangedSubview(line);
                line.WidthAnchor.ConstraintEqualTo(rows.WidthAnchor).Active = true;
            }
            none.Hidden = !passwords.IsLoaded || passwords.HasRows;
            forgetAll.Hidden = !passwords.HasRows;
        }
        bindings.Collection(passwords.Rows, _ => Show());
        bindings.Properties(passwords, [nameof(PasswordsSectionViewModel.HasRows), nameof(PasswordsSectionViewModel.IsLoaded)], Show);
        var content = Stack(vertical: true, spacing: 12, intro, none, rows, forgetAll);
        rows.WidthAnchor.ConstraintEqualTo(content.WidthAnchor).Active = true;
        return Scrolling(content);
    }

    static NSButton Button(string title, System.Windows.Input.ICommand command, Bindings bindings)
    {
        var button = NSButton.CreateButton(title, () => { });
        bindings.Command(button, command);
        return button;
    }

    static NSTextField Heading(string text)
    {
        var label = NSTextField.CreateWrappingLabel(text);
        label.Font = NSFont.SystemFontOfSize(15, NSFontWeight.Semibold)!;
        return label;
    }

    static NSTextField Section(string text)
    {
        var label = NSTextField.CreateLabel(text);
        label.Font = NSFont.SystemFontOfSize(13, NSFontWeight.Semibold)!;
        return label;
    }

    static NSTextField Secondary(string text)
    {
        var label = NSTextField.CreateWrappingLabel(text);
        label.TextColor = NSColor.SecondaryLabel;
        label.PreferredMaxLayoutWidth = 560;
        return label;
    }

    /// <summary>A tab whose content is longer than the window, scrolling from the top.</summary>
    static NSScrollView Scrolling(NSStackView content)
    {
        content.EdgeInsets = new NSEdgeInsets(20, 24, 24, 24);
        var scroll = new NSScrollView { HasVerticalScroller = true, DrawsBackground = false, DocumentView = Flipped(content) };
        scroll.DocumentView!.WidthAnchor.ConstraintEqualTo(scroll.ContentView.WidthAnchor).Active = true;
        return scroll;
    }
}
