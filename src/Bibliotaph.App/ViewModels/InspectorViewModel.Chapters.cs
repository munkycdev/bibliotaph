using Bibliotaph.Core;
using Bibliotaph.Processing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.App.ViewModels;

/// <summary>
/// A chapter in the details' "Found inside the book": its title and pages as the file numbers them, and one line per
/// field the AI found something for ("Creatures: Goblin, Ogre"), or why there is nothing.
/// </summary>
public sealed record InspectorChapter(string Title, string PagesText, IReadOnlyList<string> Lines, string? Note)
{
    public bool HasNote => Note is not null;

    /// <summary>What a screen reader says for the chapter.</summary>
    public string AccessibleName => $"{Title}, {PagesText}";
}

/// <summary>
/// The details' "Found inside the book": what the AI found reading the book chapter by chapter, kept apart from the
/// book's own details above, which it never changes. An adventure inside a file shows only its own chapters.
/// </summary>
public sealed partial class InspectorViewModel
{
    ChapterScanService? _chapters;
    ILogger? _chaptersLog;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChapters))]
    public partial IReadOnlyList<InspectorChapter> Chapters { get; private set; } = [];

    public bool HasChapters => Chapters.Count > 0;

    /// <summary>"Scan again" said it would: shown under the chapters until the details are opened again.</summary>
    [ObservableProperty]
    public partial string? ChaptersMessage { get; private set; }

    async Task LoadChaptersAsync(ILogger log)
    {
        _chapters = _metadata.Chapters;
        _chaptersLog = log;
        if (_chapters is not { } chapters || Item.IsElsewhere || Item.IsPack || !SourceFormats.HasPages(Item.Entry.Format)) return;
        try
        {
            var found = await Task.Run(() => chapters.GetChaptersAsync(Item.DocumentId, Item.IsPart ? Item.Entry.Pages : null));
            Chapters = [.. found.Select(ToChapter)];
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Couldn't read the chapters found in entry {EntryId}", Item.EntryId.Value);
        }
    }

    InspectorChapter ToChapter(ScannedChapter chapter)
    {
        var lines = ChapterFields.Describe(chapter.Tags);
        var note = chapter.Reading switch
        {
            ChapterReading.Waiting => "Not read yet.",
            ChapterReading.Failed => "The AI couldn't read this chapter.",
            _ when lines.Count == 0 => "Nothing found.",
            _ => null,
        };
        return new InspectorChapter(chapter.Title, chapter.Pages.Describe(_labels), lines, note);
    }

    /// <summary>
    /// Forgets what the AI found in the book and reads it again from the start, in the background, while AI and
    /// scanning whole books are on. The book's own details are left alone.
    /// </summary>
    [RelayCommand]
    async Task ScanAgain()
    {
        if (_chapters is not { } chapters) return;
        try
        {
            await Task.Run(() => chapters.RescanAsync(Item.DocumentId));
            Chapters = [];
            ChaptersMessage = chapters.IsOn
                ? "The AI reads this book again, chapter by chapter, when it has nothing else to do."
                : "Turn on Scan whole books in Settings > AI for the AI to read this book again.";
        }
        catch (Exception ex)
        {
            _chaptersLog?.LogError(ex, "Scanning entry {EntryId} again failed", Item.EntryId.Value);
        }
    }
}
