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
/// Pack together (multi-select plan, choices 8 to 11): one dialog whether the ticked cards make a new pack, add images
/// to a pack, or merge packs, with the pack's name ready to edit.
/// </summary>
public sealed partial class PackTogetherDialogViewModel : ObservableObject
{
    readonly PackService _packs;
    readonly IReadOnlyList<EntryId> _cards;
    readonly int _images;
    readonly int _packCount;

    PackTogetherDialogViewModel(PackService packs, IReadOnlyList<LibraryItemViewModel> cards, string name)
    {
        _packs = packs;
        _cards = [.. cards.Select(c => c.EntryId)];
        var packItems = cards.Where(c => c.IsPack).OrderBy(c => c.EntryId.Value).ToList();
        _packCount = packItems.Count;
        _images = cards.Count - _packCount;
        Name = name;
        var images = Images(_images);
        (Heading, Explanation) = _packCount switch
        {
            0 => ($"Make a pack of {images}",
                "They show as one card. A pack you make doesn't grow by itself, and Split into separate images gives each its card back."),
            1 => ($"Add {images} to {packItems[0].Title}", "They show inside the pack. New images in its folder still join it."),
            _ => ($"Merge {_packCount.ToString("N0", CultureInfo.CurrentCulture)} packs{(_images > 0 ? $" and {images}" : "")}",
                $"The newer packs join {packItems[0].Title} with their images, hearts, collections, binder items and notes. New images in their folders join it too."),
        };
    }

    /// <summary>
    /// The dialog for <paramref name="cards"/>, named after the oldest pack among them, or for images only after the
    /// folder or ZIP they share (the first image's when they don't).
    /// </summary>
    public static async Task<PackTogetherDialogViewModel> LoadAsync(PackService packs, LibraryStore library, IReadOnlyList<LibraryItemViewModel> cards)
    {
        if (cards.Where(c => c.IsPack).MinBy(c => c.EntryId.Value) is { } oldest) return new PackTogetherDialogViewModel(packs, cards, oldest.Title);
        var folders = new List<string>();
        foreach (var image in cards)
            if ((await Task.Run(() => library.GetLocationsAsync(image.DocumentId))).FirstOrDefault(l => l.State != FileLocationState.Missing) is { } place)
                folders.Add(place.ArchivePath is { } zip ? DisplayTitle.FromFileName(System.IO.Path.GetFileName(zip))
                    : DisplayTitle.FromFolderName(System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(place.FullPath)) ?? ""));
        var name = folders.Distinct(StringComparer.CurrentCultureIgnoreCase).Count() == 1 ? folders[0] : folders.FirstOrDefault() ?? "";
        return new PackTogetherDialogViewModel(packs, cards, name);
    }

    /// <summary>Raised when the dialog closes: with what Pack together did, or null when it was cancelled.</summary>
    public event EventHandler<PackTogetherDialogResult?>? Closed;

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
                0 => $"Made a pack of {Images(_images)}: {name}.",
                1 => $"Added {Images(_images)} to {name}.",
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

    static string Images(int count) => $"{count.ToString("N0", CultureInfo.CurrentCulture)} {(count == 1 ? "image" : "images")}";
}
