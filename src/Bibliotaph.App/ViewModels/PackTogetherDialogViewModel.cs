using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Processing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bibliotaph.App.ViewModels;

/// <summary>What Pack together did, for Undo, and the Library's note saying so.</summary>
public sealed record PackTogetherDialogResult(PackTogetherResult Result, string Message);

/// <summary>Pack together: the dialog for the pack's name, as <see cref="PackTogetherPlan"/> words it.</summary>
public sealed partial class PackTogetherDialogViewModel : ObservableObject
{
    readonly PackService _packs;
    readonly IReadOnlyList<EntryId> _cards;
    readonly PackTogetherPlan _plan;

    PackTogetherDialogViewModel(PackService packs, IReadOnlyList<LibraryItemViewModel> cards, PackTogetherPlan plan)
    {
        _packs = packs;
        _cards = [.. cards.Select(c => c.EntryId)];
        _plan = plan;
        Name = plan.Name;
    }

    public static async Task<PackTogetherDialogViewModel> LoadAsync(PackService packs, LibraryStore library, IReadOnlyList<LibraryItemViewModel> cards) =>
        new(packs, cards, await Task.Run(() => PackTogetherPlan.LoadAsync(library, [.. cards.Select(c => c.Entry)])));

    /// <summary>Raised when the dialog closes: with what Pack together did, or null when it was cancelled.</summary>
    public event EventHandler<PackTogetherDialogResult?>? Closed;

    /// <summary>"IMAGE PACK", or "PACK" when PDFs go in it too.</summary>
    public string Eyebrow => _plan.Eyebrow;

    public string Heading => _plan.Heading;

    public string Explanation => _plan.Explanation;

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
                Problem = PackTogetherPlan.Gone;
                return;
            }
            Closed?.Invoke(this, new PackTogetherDialogResult(result, _plan.Done(name)));
        }
        finally
        {
            IsPacking = false;
        }
    }

    [RelayCommand]
    void Cancel() => Closed?.Invoke(this, null);
}
