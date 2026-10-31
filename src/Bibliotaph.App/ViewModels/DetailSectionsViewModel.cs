using Bibliotaph.Catalog;
using Bibliotaph.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;

namespace Bibliotaph.App.ViewModels;

/// <summary>
/// Which sections of a book's details are open, shared by every book's details so a section stays as it was left.
/// Kept for this PC; until the user folds or opens one, <see cref="DetailSections.FoldedAtFirst"/> are folded.
/// </summary>
public sealed partial class DetailSectionsViewModel(SettingsStore store, ILogger<DetailSectionsViewModel> log) : ObservableObject
{
    bool _loaded;
    bool _loading;

    [ObservableProperty] public partial bool Adventures { get; set; } = true;
    [ObservableProperty] public partial bool InFile { get; set; } = true;
    [ObservableProperty] public partial bool Pack { get; set; } = true;
    [ObservableProperty] public partial bool Found { get; set; } = true;
    [ObservableProperty] public partial bool Collections { get; set; } = true;
    [ObservableProperty] public partial bool Details { get; set; } = true;
    [ObservableProperty] public partial bool AboutFile { get; set; }
    [ObservableProperty] public partial bool Where { get; set; }
    [ObservableProperty] public partial bool Copies { get; set; }
    [ObservableProperty] public partial bool Indexing { get; set; }

    /// <summary>Reads what was left folded, the first time any book's details open.</summary>
    public async Task LoadAsync()
    {
        if (_loaded) return;
        _loaded = true;
        string? stored;
        try
        {
            stored = await store.GetAsync(SettingKeys.DetailsFolded);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Reading which details sections are folded failed");
            return;
        }
        var folded = DetailSections.Read(stored);
        _loading = true;
        Adventures = !folded.Contains(DetailSections.Adventures);
        InFile = !folded.Contains(DetailSections.InFile);
        Pack = !folded.Contains(DetailSections.Pack);
        Found = !folded.Contains(DetailSections.Found);
        Collections = !folded.Contains(DetailSections.Collections);
        Details = !folded.Contains(DetailSections.Details);
        AboutFile = !folded.Contains(DetailSections.File);
        Where = !folded.Contains(DetailSections.Where);
        Copies = !folded.Contains(DetailSections.Copies);
        Indexing = !folded.Contains(DetailSections.Indexing);
        _loading = false;
    }

    IEnumerable<string> Folded()
    {
        if (!Adventures) yield return DetailSections.Adventures;
        if (!InFile) yield return DetailSections.InFile;
        if (!Pack) yield return DetailSections.Pack;
        if (!Found) yield return DetailSections.Found;
        if (!Collections) yield return DetailSections.Collections;
        if (!Details) yield return DetailSections.Details;
        if (!AboutFile) yield return DetailSections.File;
        if (!Where) yield return DetailSections.Where;
        if (!Copies) yield return DetailSections.Copies;
        if (!Indexing) yield return DetailSections.Indexing;
    }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (!_loading) _ = SaveAsync();
    }

    async Task SaveAsync()
    {
        try
        {
            await store.SetAsync(SettingKeys.DetailsFolded, DetailSections.Write(Folded()));
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Saving which details sections are folded failed");
        }
    }
}
