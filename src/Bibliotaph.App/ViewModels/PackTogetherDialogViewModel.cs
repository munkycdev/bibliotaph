using System.Globalization;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Processing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bibliotaph.App.ViewModels;

/// <summary>What Pack together did, for Undo, and the Library's note saying so.</summary>
public sealed record PackTogetherDialogResult(PackTogetherResult Result, string Message);

/// <summary>
/// Pack together: one dialog whether the ticked cards make a new pack, add files to a pack, or merge packs, with
/// the pack's name ready to edit. Images and PDFs alike go in, such as a party's character sheets and their portraits;
/// with a PDF among them it speaks of files rather than images.
/// </summary>
public sealed partial class PackTogetherDialogViewModel : ObservableObject
{
    readonly PackService _packs;
    readonly IReadOnlyList<EntryId> _cards;
    readonly int _files;
    readonly bool _pdfsTicked;
    readonly int _packCount;

    PackTogetherDialogViewModel(PackService packs, IReadOnlyList<LibraryItemViewModel> cards, string name)
    {
        _packs = packs;
        _cards = [.. cards.Select(c => c.EntryId)];
        var packItems = cards.Where(c => c.IsPack).OrderBy(c => c.EntryId.Value).ToList();
        _packCount = packItems.Count;
        _files = cards.Count - _packCount;
        _pdfsTicked = cards.Any(c => !c.IsPack && c.Entry.Format == SourceFormats.Pdf);
        var withPdfs = _pdfsTicked || packItems.Any(p => p.HasPdfMembers);
        Eyebrow = withPdfs ? "PACK" : "IMAGE PACK";
        Name = name;
        var files = Count(_files);
        var split = withPdfs ? "Split into separate files" : "Split into separate images";
        (Heading, Explanation) = _packCount switch
        {
            0 => ($"Make a pack of {files}",
                $"They show as one card. A pack you make doesn't grow by itself, and {split} gives each its card back."),
            1 => ($"Add {files} to {packItems[0].Title}", "They show inside the pack. New images in its folder still join it."),
            _ => ($"Merge {_packCount.ToString("N0", CultureInfo.CurrentCulture)} packs{(_files > 0 ? $" and {files}" : "")}",
                $"The newer packs join {packItems[0].Title} with their {(withPdfs ? "files" : "images")}, hearts, collections, binder items and notes. New images in their folders join it too."),
        };
    }

    /// <summary>
    /// The dialog for <paramref name="cards"/>, named after the oldest pack among them, or for files only after the
    /// folder or ZIP they share (the first file's when they don't).
    /// </summary>
    public static async Task<PackTogetherDialogViewModel> LoadAsync(PackService packs, LibraryStore library, IReadOnlyList<LibraryItemViewModel> cards)
    {
        if (cards.Where(c => c.IsPack).MinBy(c => c.EntryId.Value) is { } oldest) return new PackTogetherDialogViewModel(packs, cards, oldest.Title);
        var folders = new List<string>();
        foreach (var file in cards)
            if ((await Task.Run(() => library.GetLocationsAsync(file.DocumentId))).FirstOrDefault(l => l.State != FileLocationState.Missing) is { } place)
                folders.Add(place.ArchivePath is { } zip ? DisplayTitle.FromFileName(System.IO.Path.GetFileName(zip))
                    : DisplayTitle.FromFolderName(System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(place.FullPath)) ?? ""));
        var name = folders.Distinct(StringComparer.CurrentCultureIgnoreCase).Count() == 1 ? folders[0] : folders.FirstOrDefault() ?? "";
        return new PackTogetherDialogViewModel(packs, cards, name);
    }

    /// <summary>Raised when the dialog closes: with what Pack together did, or null when it was cancelled.</summary>
    public event EventHandler<PackTogetherDialogResult?>? Closed;

    /// <summary>"IMAGE PACK", or "PACK" when PDFs go in it too.</summary>
    public string Eyebrow { get; }

    public string Heading { get; }

    public string Explanation { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PackCommand))]
    public partial string Name { get; set; }

    /// <summary>Why the cards couldn't be packed, such as one having changed since they were ticked.</summary>
    [ObservableProperty]
    public partial string? Problem { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PackCommand))]
    public partial bool IsPacking { get; private set; }

    bool CanPack => !IsPacking && Name.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanPack))]
    async Task Pack()
    {
        IsPacking = true;
        Problem = null;
        try
        {
            var name = Name.Trim();
            if (await Task.Run(() => _packs.PackTogetherAsync(_cards, name)) is not { } result)
            {
                Problem = "These cards can't be packed any more: one of them has changed. Close this and tick them again.";
                return;
            }
            var message = _packCount switch
            {
                0 => $"Made a pack of {Count(_files)}: {name}.",
                1 => $"Added {Count(_files)} to {name}.",
                _ => $"Merged {_packCount.ToString("N0", CultureInfo.CurrentCulture)} packs into {name}.",
            };
            Closed?.Invoke(this, new PackTogetherDialogResult(result, message));
        }
        finally
        {
            IsPacking = false;
        }
    }

    [RelayCommand]
    void Cancel() => Closed?.Invoke(this, null);

    /// <summary>"2 images", or "2 files" when a PDF is among the ticked cards.</summary>
    string Count(int count) => _pdfsTicked ? LibraryItemViewModel.Files(count) : LibraryItemViewModel.Images(count);
}
