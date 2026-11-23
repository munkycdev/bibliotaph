using AppKit;
using Bibliotaph.Mac.Binding;
using Bibliotaph.Presentation.Services;
using Bibliotaph.Presentation.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Bibliotaph.Mac.Views;

/// <summary>Settings' Processing, Backup and Passwords panes, each the Windows section's view model, as grouped forms.</summary>
public sealed partial class SettingsWindow
{
    /// <summary>Each step of reading the library with its bar and Run again, what is happening now, and pausing.</summary>
    NSScrollView ProcessingTab()
    {
        var processing = _services.GetRequiredService<ProcessingSectionViewModel>();
        var bindings = Load(processing);
        var activity = processing.Activity;

        var form = new Form();
        var empty = form.Section(null, "Nothing to process until you add a folder.");
        empty.Row("No library folders yet", Button("Add a Folder in Library", processing.OpenLibraryCommand, bindings));

        var progress = form.Section("Progress", "Only while Bibliotaph is open. Closing the app stops it; reopening resumes it.");
        foreach (var phase in activity.Phases) Phase(progress, processing, phase, bindings);
        bindings.Properties(activity, [nameof(LibraryActivity.Summary), nameof(LibraryActivity.TimeLeftText)], () =>
            progress.Title.StringValue = activity.TimeLeftText.Length == 0 ? activity.Summary : $"{activity.Summary} · {activity.TimeLeftText}");
        bindings.Property(processing, nameof(ProcessingSectionViewModel.RunAgainNote), () =>
            progress.Note(processing.RunAgainNote.Length > 0 ? processing.RunAgainNote : "Only while Bibliotaph is open. Closing the app stops it; reopening resumes it."));

        var now = form.Section("Now");
        var nowText = FormText.Note("", 12);
        var nowRow = now.Wide(nowText);
        var attention = now.Row("Files needing attention", Button("Show…", processing.OpenFilesNeedingAttentionCommand, bindings));
        var reading = now.Row("What the AI is reading", Button("Show…", processing.OpenAiReadingCommand, bindings));
        bindings.Properties(activity, [nameof(LibraryActivity.Now), nameof(LibraryActivity.Notes), nameof(LibraryActivity.AiWaiting)], () =>
        {
            nowText.StringValue = string.Join("\n", activity.Now);
            nowRow.Hidden = activity.Now.Count == 0;
            attention.ShowDetail(activity.Notes);
            now.Note(activity.IsAiWaiting ? $"Cataloguing with AI is waiting: {activity.AiWaiting}" : null);
            now.Footer.TextColor = NSColor.SystemOrange;
        });
        reading.ShowDetail("The books it is reading now, and those that failed");

        var pause = form.Section("Pause");
        var index = Button("", processing.ToggleIndexingCommand, bindings);
        var ocr = Button("", processing.ToggleOcrCommand, bindings);
        var ai = Button("", processing.ToggleAiCommand, bindings);
        var indexRow = pause.Row("Indexing", index);
        var ocrRow = pause.Row("Reading scanned pages", ocr);
        var aiRow = pause.Row("Cataloguing with AI", ai);
        bindings.Properties(activity, [nameof(LibraryActivity.IndexPaused), nameof(LibraryActivity.OcrPaused), nameof(LibraryActivity.AiPaused),
            nameof(LibraryActivity.ShowIndexPause), nameof(LibraryActivity.ShowOcrPause), nameof(LibraryActivity.ShowAiPause)], () =>
        {
            index.Title = activity.IndexPaused ? "Resume" : "Pause";
            ocr.Title = activity.OcrPaused ? "Resume" : "Pause";
            ai.Title = activity.AiPaused ? "Resume" : "Pause";
            indexRow.Hidden = !activity.ShowIndexPause;
            ocrRow.Hidden = !activity.ShowOcrPause;
            aiRow.Hidden = !activity.ShowAiPause;
            pause.View.Hidden = indexRow.Hidden && ocrRow.Hidden && aiRow.Hidden;
        });

        // What goes online, as a note at the foot of the pane: a box would look like a setting, and there is nothing to set.
        var lookUp = form.Section("What Bibliotaph looks up online");
        lookUp.Title.Font = NSFont.SystemFontOfSize(11, NSFontWeight.Semibold)!;
        lookUp.Title.TextColor = NSColor.SecondaryLabel;
        bindings.Property(processing, nameof(ProcessingSectionViewModel.LookUpExplanation), () =>
            lookUp.Note($"{processing.LookUpExplanation} Your files, notes and collections stay on this device."));

        bindings.Property(processing, nameof(ProcessingSectionViewModel.HasFolders), () =>
        {
            empty.View.Hidden = processing.HasFolders;
            foreach (var section in new[] { progress, now, lookUp }) section.View.Hidden = !processing.HasFolders;
        });
        return Pane(form);
    }

    /// <summary>A step: its name and what it does; its bar, how far it has got, and Run again when it can be.</summary>
    static void Phase(FormSection section, ProcessingSectionViewModel processing, PhaseProgress phase, Bindings bindings)
    {
        var bar = new NSProgressIndicator { Style = NSProgressIndicatorStyle.Bar, MinValue = 0, MaxValue = 100, ControlSize = NSControlSize.Small };
        bar.WidthAnchor.ConstraintEqualTo(120).Active = true;
        var status = NSTextField.CreateLabel("");
        status.Font = NSFont.MonospacedDigitSystemFontOfSize(11, NSFontWeight.Regular)!;
        status.TextColor = NSColor.SecondaryLabel;
        status.Alignment = NSTextAlignment.Right;
        status.WidthAnchor.ConstraintEqualTo(150).Active = true;
        status.LineBreakMode = NSLineBreakMode.TruncatingTail;
        var runAgain = NSButton.CreateButton(NSImage.GetSystemSymbol("arrow.clockwise", "Run again")!, () => processing.RunAgainCommand.Execute(phase));
        runAgain.BezelStyle = NSBezelStyle.Inline;
        runAgain.Bordered = false;
        runAgain.WidthAnchor.ConstraintEqualTo(20).Active = true;
        section.Row(phase.Label, phase.Detail, bar, status, runAgain);
        bindings.Properties(phase, [nameof(PhaseProgress.Percent), nameof(PhaseProgress.IsIndeterminate), nameof(PhaseProgress.Status),
            nameof(PhaseProgress.State), nameof(PhaseProgress.RunAgain)], () =>
        {
            bar.Indeterminate = phase.IsIndeterminate;
            if (phase.IsIndeterminate && phase.State == PhaseState.Running) bar.StartAnimation(null);
            else bar.StopAnimation(null);
            bar.DoubleValue = phase.Percent;
            status.StringValue = phase.Status;
            status.ToolTip = phase.Status;
            // Kept in place, so the bars line up whether or not a step can run again.
            runAgain.AlphaValue = phase.RunAgain is null ? 0 : 1;
            runAgain.Enabled = phase.RunAgain is not null;
            runAgain.ToolTip = phase.RunAgain?.Hint ?? "";
        });
    }

    /// <summary>Back up now, restore from a backup with the folders it names, and export the catalog.</summary>
    NSScrollView BackupTab()
    {
        var backup = _services.GetRequiredService<BackupSectionViewModel>();
        var bindings = Load(backup);

        var form = new Form();
        var backUp = form.Section("Back up",
            "A backup holds your catalog: folders, corrections, collections, binders and notes. Your books aren't in it; they stay where they are.");
        var now = backUp.Row("Back up now", Button("Back Up…", backup.BackUpNowCommand, bindings));
        var include = FormText.Switch(on => backup.IncludeIndex = on);
        backUp.Row("Include the search index", "Larger backups, but a restored library is searchable straight away", include);
        bindings.Property(backup, nameof(BackupSectionViewModel.IncludeIndex), () => FormText.Set(include, backup.IncludeIndex));
        backUp.Row("Backups", Button("Show in Finder", backup.OpenBackupsFolderCommand, bindings));
        bindings.Properties(backup, [nameof(BackupSectionViewModel.BackupNote), nameof(BackupSectionViewModel.LastBackup)],
            () => now.ShowDetail(backup.BackupNote ?? backup.LastBackup));

        var restore = form.Section("Restore",
            "Puts your catalog back as it was when a backup was made, on this Mac or a new one. Your current catalog is backed up first. Passwords for protected PDFs and AI keys aren't in backups; Bibliotaph asks for them again when it needs them.");
        var choose = restore.Row("Restore from a backup", Button("Choose…", backup.ChooseBackupCommand, bindings));
        var folders = form.Section("Folders in this backup");
        var restoreButtons = form.Buttons(Button("Cancel", backup.CancelRestoreCommand, bindings), Button("Restore and Restart…", backup.RestoreCommand, bindings));
        var folderBindings = new Bindings();
        bindings.Add(() => folderBindings.Dispose());
        void ShowFolders()
        {
            folderBindings.Dispose();
            folderBindings = new Bindings();
            folders.Clear();
            foreach (var folder in backup.RestoreFolders) RestoreFolder(folders, backup, folder, folderBindings);
        }
        bindings.Collection(backup.RestoreFolders, _ => ShowFolders());
        bindings.Properties(backup, [nameof(BackupSectionViewModel.RestoreSummary), nameof(BackupSectionViewModel.RestoreProblem),
            nameof(BackupSectionViewModel.HasPreview)], () =>
        {
            choose.ShowDetail(backup.RestoreProblem ?? backup.RestoreSummary);
            choose.Detail.TextColor = backup.RestoreProblem is null ? NSColor.SecondaryLabel : NSColor.SystemOrange;
            folders.View.Hidden = restoreButtons.Hidden = !backup.HasPreview;
            ShowFolders();
        });

        var export = form.Section("Export",
            "CSV has a row per book: title, system, type, levels, publisher, tags, collections and where its file is. JSON has everything in the catalog, for other programs.");
        export.Row("For a spreadsheet (CSV)", Button("Export…", backup.ExportCsvCommand, bindings));
        export.Row("Everything (JSON)", Button("Export…", backup.ExportJsonCommand, bindings));
        bindings.Property(backup, nameof(BackupSectionViewModel.ExportNote), () => export.Note(backup.ExportNote ??
            "CSV has a row per book: title, system, type, levels, publisher, tags, collections and where its file is. JSON has everything in the catalog, for other programs."));

        // A development aid, as on Windows: only with --dev, and last, so it isn't chosen by accident.
        if (_services.GetRequiredService<DevMode>().IsOn)
        {
            var startOver = _services.GetRequiredService<StartOverSectionViewModel>();
            var over = form.Section("Start over",
                "Forgets your library folders, the index, covers, settings, remembered PDF passwords, AI keys and the web library connection, then restarts as if newly installed. Your books and other files are not touched.");
            over.Row("Start over", Button("Start Over…", startOver.StartOverCommand, bindings));
        }
        return Pane(form);
    }

    /// <summary>A folder the backup names: found here, or pointed to its new place, or left offline.</summary>
    static void RestoreFolder(FormSection section, BackupSectionViewModel backup, RestoreFolderItem folder, Bindings bindings)
    {
        var point = NSButton.CreateButton("Locate…", () => backup.PointRestoredFolderCommand.Execute(folder));
        var leave = NSButton.CreateButton("Leave Offline", () => backup.LeaveOfflineCommand.Execute(folder));
        var row = section.Row(Name(folder.Path), folder.Path, point, leave);
        row.OneLine();
        var state = row.AddDetail();
        bindings.Properties(folder, [nameof(RestoreFolderItem.NewPlace), nameof(RestoreFolderItem.Problem)], () =>
        {
            state.StringValue = folder.HasProblem ? folder.Problem : $"{folder.Files} · {folder.Status}";
            state.TextColor = folder.HasProblem ? NSColor.SystemOrange : NSColor.SecondaryLabel;
            point.Hidden = folder.Found;
            leave.Hidden = !folder.IsPointed;
        });
    }

    /// <summary>The books whose PDF password is remembered in the Keychain, each with Forget, and Forget All.</summary>
    NSScrollView PasswordsTab()
    {
        var passwords = _services.GetRequiredService<PasswordsSectionViewModel>();
        var bindings = Load(passwords);
        var form = new Form();
        var list = form.Section("Remembered passwords",
            "PDF passwords you chose to remember, kept in your Keychain on this Mac, never in Bibliotaph's own files. Forget one and you'll be asked for it the next time you open the book; text already read stays searchable.");
        var forgetAll = Button("Forget All…", passwords.ForgetAllCommand, bindings);
        var buttons = form.Buttons(forgetAll);
        void Show()
        {
            list.Clear();
            foreach (var row in passwords.Rows)
            {
                var line = list.Row(row.Title, NSButton.CreateButton("Forget", () => passwords.ForgetCommand.Execute(row)));
                line.Label.LineBreakMode = NSLineBreakMode.TruncatingTail;
            }
            if (passwords.IsLoaded && !passwords.HasRows) list.Row("No passwords are remembered.");
            buttons.Hidden = !passwords.HasRows;
        }
        bindings.Collection(passwords.Rows, _ => Show());
        bindings.Properties(passwords, [nameof(PasswordsSectionViewModel.HasRows), nameof(PasswordsSectionViewModel.IsLoaded)], Show);
        return Pane(form);
    }

    static NSButton Button(string title, System.Windows.Input.ICommand command, Bindings bindings)
    {
        var button = NSButton.CreateButton(title, () => { });
        bindings.Command(button, command);
        return button;
    }
}
